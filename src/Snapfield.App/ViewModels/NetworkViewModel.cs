using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Snapfield.App.Mvvm;
using Snapfield.Core.Persistence;
using Snapfield.Platform.Input;
using Snapfield.Platform.Monitors;
using Snapfield.Platform.Net;

namespace Snapfield.App.ViewModels;

/// <summary>Which page of the network window is showing.</summary>
public enum NetMode { Choose, Receiver, Controller }

/// <summary>
/// Drives the network window across three pages:
///   • Choose  — full-bleed split: one click picks 조작 기기 (controller) or 수신 기기
///               (receiver); picking receiver starts listening immediately
///   • Controller — a list of recent machines + a form to add a new one
///   • Receiver   — this PC's IP + pairing code, with a live "waiting / controlled" state
/// </summary>
public sealed class NetworkViewModel : ObservableObject
{
    private readonly string _machineId = Environment.MachineName;
    private NetworkSession? _session;

    public NetworkViewModel()
    {
        ChooseControllerCommand = new RelayCommand(() => Mode = NetMode.Controller);
        ChooseReceiverCommand = new RelayCommand(() =>
        {
            Mode = NetMode.Receiver;
            if (!IsActive) Listen(); // one step: picking the role starts waiting immediately
        });
        BackCommand = new RelayCommand(() => { Stop(); ShowNewForm = false; Mode = NetMode.Choose; });
        ListenCommand = new RelayCommand(Listen, () => !IsActive);
        // These may run while already connected — the controller is a hub and can
        // add more receivers.
        ConnectCommand = new RelayCommand(Connect, () => !string.IsNullOrWhiteSpace(RemoteHost) && !string.IsNullOrWhiteSpace(ControllerPin));
        ConnectRecentCommand = new RelayCommand<RecentConnection>(ConnectRecent, r => r is not null);
        PickDiscoveredCommand = new RelayCommand<DiscoveredItem>(PickDiscovered, d => d is not null);
        ShowNewFormCommand = new RelayCommand(() => { ShowAdvanced = false; ShowNewForm = true; });
        CloseSheetCommand = new RelayCommand(() => ShowNewForm = false);
        ToggleAdvancedCommand = new RelayCommand(() => ShowAdvanced = !ShowAdvanced);
        StopCommand = new RelayCommand(Stop, () => IsActive);

        MachineName = _machineId;
        LocalIps = GetLocalIps();

        var s = SettingsStore.Load();
        if (string.IsNullOrEmpty(s.ReceiverPin))
        {
            s = s with { ReceiverPin = Random.Shared.Next(100000, 999999).ToString() };
            SettingsStore.Save(s);
        }
        ReceiverPin = s.ReceiverPin;
        _controllerPin = s.ControllerPin;
        foreach (var r in s.Recent) RecentConnections.Add(r);
    }

    public ICommand ChooseControllerCommand { get; }
    public ICommand ChooseReceiverCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ListenCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand ConnectRecentCommand { get; }
    public ICommand PickDiscoveredCommand { get; }
    public ICommand ShowNewFormCommand { get; }
    public ICommand CloseSheetCommand { get; }
    public ICommand ToggleAdvancedCommand { get; }
    public ICommand StopCommand { get; }

    public string MachineName { get; }
    public string LocalIps { get; }
    public string ReceiverPin { get; }
    public ObservableCollection<RecentConnection> RecentConnections { get; } = new();
    public bool HasRecent => RecentConnections.Count > 0;
    public bool NoRecent => RecentConnections.Count == 0;

    /// <summary>A receiver found on the LAN via its beacon.</summary>
    public sealed class DiscoveredItem
    {
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public long LastSeen { get; set; }
    }

    public ObservableCollection<DiscoveredItem> Discovered { get; } = new();
    public bool HasDiscovered => Discovered.Count > 0;
    /// <summary>Controller is scanning but hasn't heard a receiver yet.</summary>
    public bool Scanning => _discovery is not null && Discovered.Count == 0;

    private DiscoveryListener? _discovery;

    /// <summary>Receivers currently connected (controller hub can hold several).</summary>
    public ObservableCollection<string> ConnectedPeers { get; } = new();
    public bool HasPeers => ConnectedPeers.Count > 0;

    /// <summary>Transient notification for the toast stack: (title, message, kind).
    /// Kind is one of Ok / Warn / Err / Tip. Always raised on the UI thread.</summary>
    public event Action<string, string, string>? Toast;

    // ── Global status pill (header, visible on every tab) ────────────────────
    private string _pillText = "연결 안 됨";
    public string PillText { get => _pillText; private set => SetField(ref _pillText, value); }

    private string _pillKind = "Off"; // Off | Ok | Live
    public string PillKind { get => _pillKind; private set => SetField(ref _pillKind, value); }

    private bool _remoteActive;          // engine says the cursor is on a remote machine
    private string _activeRemoteName = "";
    private bool _captureTipShown;       // Ctrl×3 tip shows once per app run

    private void UpdatePill()
    {
        string text, kind;
        if (!_isActive) { text = "연결 안 됨"; kind = "Off"; }
        else if (_mode == NetMode.Receiver)
        {
            if (_isBeingControlled) { text = "제어당하는 중"; kind = "Live"; }
            else { text = "연결 대기 중"; kind = "Ok"; }
        }
        else if (_remoteActive) { text = $"원격 조작 중 · {_activeRemoteName}"; kind = "Live"; }
        else if (ConnectedPeers.Count == 1) { text = $"{ConnectedPeers[0]} 연결됨"; kind = "Ok"; }
        else if (ConnectedPeers.Count > 1) { text = $"{ConnectedPeers[0]} 외 {ConnectedPeers.Count - 1}대 연결됨"; kind = "Ok"; }
        else { text = "연결 중 …"; kind = "Off"; }
        PillText = text;
        PillKind = kind;
    }

    // ── Page routing ──────────────────────────────────────────────────────────
    private NetMode _mode = NetMode.Choose;
    public NetMode Mode
    {
        get => _mode;
        set
        {
            if (!SetField(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsChoosePage));
            OnPropertyChanged(nameof(IsReceiverPage));
            OnPropertyChanged(nameof(IsControllerPage));
            OnPropertyChanged(nameof(ActingAsReceiver));
            if (value == NetMode.Controller) StartDiscovery(); else StopDiscovery();
        }
    }
    public bool IsChoosePage => Mode == NetMode.Choose;
    public bool IsReceiverPage => Mode == NetMode.Receiver;
    public bool IsControllerPage => Mode == NetMode.Controller;

    // ── Shared / session state ────────────────────────────────────────────────
    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        private set { if (SetField(ref _isActive, value)) { OnPropertyChanged(nameof(IsIdle)); OnPropertyChanged(nameof(IsWaiting)); OnPropertyChanged(nameof(ActingAsReceiver)); } }
    }
    public bool IsIdle => !_isActive;

    /// <summary>This machine is running as a receiver — the controller owns the layout,
    /// so its calibration plane is read-only (mirrors what the controller sends).</summary>
    public bool ActingAsReceiver => _isActive && _mode == NetMode.Receiver;

    private bool _isBeingControlled;
    public bool IsBeingControlled
    {
        get => _isBeingControlled;
        private set { if (SetField(ref _isBeingControlled, value)) OnPropertyChanged(nameof(IsWaiting)); }
    }
    /// <summary>Receiver has started listening but nobody is driving it yet.</summary>
    public bool IsWaiting => _isActive && !_isBeingControlled;

    private bool _showNewForm;
    /// <summary>Whether the bottom "new connection" sheet is open.</summary>
    public bool ShowNewForm { get => _showNewForm; set => SetField(ref _showNewForm, value); }

    private bool _showAdvanced;
    /// <summary>Port lives under "고급" — hidden by default (almost always 45654).</summary>
    public bool ShowAdvanced { get => _showAdvanced; set => SetField(ref _showAdvanced, value); }

    private int _port = 45654;
    public int Port { get => _port; set => SetField(ref _port, value); }

    private string _remoteHost = "";
    public string RemoteHost { get => _remoteHost; set => SetField(ref _remoteHost, value); }

    private string _controllerPin = "";
    public string ControllerPin
    {
        get => _controllerPin;
        set { if (SetField(ref _controllerPin, value)) PinError = false; } // retyping clears the error
    }

    private bool _pinError;
    /// <summary>The last connect attempt failed authentication — the pin slots turn red.</summary>
    public bool PinError { get => _pinError; private set => SetField(ref _pinError, value); }

    private string _pinErrorText = "";
    public string PinErrorText { get => _pinErrorText; private set => SetField(ref _pinErrorText, value); }

    private double _sensitivity = 1.0;
    public double Sensitivity
    {
        get => _sensitivity;
        set { if (SetField(ref _sensitivity, value) && _session is not null) _session.Sensitivity = value; }
    }

    private string _status = "";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private string _side = "—";
    public string Side { get => _side; private set => SetField(ref _side, value); }

    private Brush _sideBrush = Brushes.Gray;
    public Brush SideBrush { get => _sideBrush; private set => SetField(ref _sideBrush, value); }

    private string _detail = "";
    public string Detail { get => _detail; private set => SetField(ref _detail, value); }

    // ── Actions ──────────────────────────────────────────────────────────────
    private void Listen()
    {
        var vm = this;
        new Thread(() =>
        {
            var msg = FirewallHelper.EnsureRule();
            Application.Current?.Dispatcher.BeginInvoke(() => { if (vm.IsActive) vm.Status = msg; });
        }) { IsBackground = true, Name = "Snapfield.Firewall" }.Start();

        EnsureSession();
        _session!.Listen(Port);
        IsActive = true;
        UpdatePill();
        SettingsStore.Save(SettingsStore.Load() with { LastRole = "Receiver", LastPort = Port });
    }

    private void Connect()
    {
        var host = RemoteHost.Trim();
        var pin = ControllerPin.Trim();
        if (host.Length == 0 || pin.Length == 0) return;
        ShowNewForm = false;              // close the sheet
        EnsureSession();
        _session!.Connect(host, Port, pin); // adds a peer (hub can hold several)
        IsActive = true;
        UpdatePill();
        SettingsStore.Save(SettingsStore.Load() with { LastRole = "Controller", LastHost = host, LastPort = Port, ControllerPin = pin });
        Remember(host, Port, pin, host);
    }

    private void ConnectRecent(RecentConnection? r)
    {
        if (r is null) return;
        RemoteHost = r.Host;
        Port = r.Port;
        ControllerPin = r.Pin;
        Connect();
    }

    private void PickDiscovered(DiscoveredItem? d)
    {
        if (d is null) return;
        // Fill the address and open the sheet so the user just enters the code.
        RemoteHost = d.Host;
        Port = d.Port;
        ShowAdvanced = false;
        ShowNewForm = true;
    }

    // ── LAN discovery (controller side) ───────────────────────────────────────
    private void StartDiscovery()
    {
        if (_discovery is not null) return;
        _discovery = new DiscoveryListener();
        _discovery.Found += OnPeerFound;
        _discovery.Start();
        OnPropertyChanged(nameof(Scanning));
    }

    private void StopDiscovery()
    {
        _discovery?.Dispose();
        _discovery = null;
        if (Discovered.Count > 0) Discovered.Clear();
        OnPropertyChanged(nameof(HasDiscovered));
        OnPropertyChanged(nameof(Scanning));
    }

    private void OnPeerFound(DiscoveredPeer peer) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (peer.Name == _machineId) return; // ignore ourselves
            var now = Environment.TickCount64;

            // Drop stale entries (no beacon in ~6s).
            for (var i = Discovered.Count - 1; i >= 0; i--)
                if (now - Discovered[i].LastSeen > 6000) Discovered.RemoveAt(i);

            var existing = Discovered.FirstOrDefault(x => x.Host == peer.Host);
            if (existing is null)
                Discovered.Add(new DiscoveredItem { Name = peer.Name, Host = peer.Host, Port = peer.Port, LastSeen = now });
            else { existing.Name = peer.Name; existing.Port = peer.Port; existing.LastSeen = now; }

            OnPropertyChanged(nameof(HasDiscovered));
            OnPropertyChanged(nameof(Scanning));
        });

    private void Remember(string host, int port, string pin, string name)
    {
        SettingsStore.RememberConnection(new RecentConnection { Host = host, Port = port, Pin = pin, Name = name });
        ReloadRecent();
    }

    private void ReloadRecent()
    {
        RecentConnections.Clear();
        foreach (var r in SettingsStore.Load().Recent) RecentConnections.Add(r);
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(NoRecent));
    }

    public void RestoreLastSession()
    {
        var s = SettingsStore.Load();
        switch (s.LastRole)
        {
            case "Receiver":
                Port = s.LastPort; Mode = NetMode.Receiver; Listen();
                break;
            case "Controller" when !string.IsNullOrWhiteSpace(s.LastHost):
                Port = s.LastPort; RemoteHost = s.LastHost; ControllerPin = s.ControllerPin;
                Mode = NetMode.Controller; Connect();
                break;
        }
    }

    private void EnsureSession()
    {
        if (_session is not null) return;
        var monitors = new MonitorEnumerator().Enumerate();
        _session = new NetworkSession(_machineId, monitors) { Sensitivity = Sensitivity, ReceiverPin = ReceiverPin };
        _session.Status += OnStatus;
        _session.EngineStatus += OnEngineStatus;
        _session.ReceiverActivity += OnReceiverActivity;
        _session.PeersChanged += OnPeersChanged;
        _session.AuthFailed += OnAuthFailed;
        if (monitors.Count == 0)
            Status = "경고: 이 PC의 모니터를 하나도 인식하지 못했습니다!";
    }

    private void Stop()
    {
        _session?.Dispose();
        _session = null;
        IsActive = false;
        IsBeingControlled = false;
        _remoteActive = false;
        ConnectedPeers.Clear();
        OnPropertyChanged(nameof(HasPeers));
        UpdatePill();
        Status = "";
        Side = "—";
        Detail = "";
        SideBrush = Brushes.Gray;
        if (Mode == NetMode.Controller) StartDiscovery();
    }

    public void ShutDown() { Stop(); StopDiscovery(); }

    // ── Session callbacks ─────────────────────────────────────────────────────
    private void OnStatus(string s) =>
        Application.Current?.Dispatcher.BeginInvoke(() => Status = s);

    private void OnAuthFailed(string text) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Reopen the sheet so the user fixes the code in place — the error
            // shows under the slots, not as a gray status line after the fact.
            PinErrorText = string.IsNullOrWhiteSpace(text) ? "연결 코드가 일치하지 않습니다." : text;
            ShowAdvanced = false;
            ShowNewForm = true;
            PinError = true; // after ShowNewForm so the view can focus the slots on error
        });

    private void OnPeersChanged(IReadOnlyList<string> names) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Toast the diff. Names are machine ids that only appear post-Hello,
            // so add/remove maps cleanly to connect/disconnect. A manual Stop()
            // clears the list directly and never comes through here.
            var old = ConnectedPeers.ToList();
            foreach (var n in names.Where(n => !old.Contains(n)))
                Toast?.Invoke($"{n} 연결됨", "커서를 화면 끝으로 밀어 넘어가세요.", "Ok");
            foreach (var n in old.Where(o => !names.Contains(o)))
                Toast?.Invoke($"{n} 연결 끊김", "재연결을 시도합니다 …", "Err");

            ConnectedPeers.Clear();
            foreach (var n in names) ConnectedPeers.Add(n);
            OnPropertyChanged(nameof(HasPeers));
            UpdatePill();
        });

    private void OnEngineStatus(EngineStatus s) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var remote = s.Captured || s.ActiveIsRemote;
            if (remote && !_captureTipShown)
            {
                _captureTipShown = true; // the escape hatch matters exactly once — right when it's first needed
                Toast?.Invoke("원격 조작 시작", "돌아오려면 화면 끝으로 다시 밀거나 Ctrl 키를 3번 연타하세요.", "Tip");
            }
            _remoteActive = remote;
            _activeRemoteName = remote ? s.ActiveMonitor : "";
            UpdatePill();
            Side = remote ? $"{s.ActiveMonitor} 조작 중" : "이 PC (대기)";
            Detail = $"연결된 기기 {ConnectedPeers.Count}대   ·   ({s.VirtualXMm:0}, {s.VirtualYMm:0} mm)";
            SideBrush = remote
                ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x2A))
                : new SolidColorBrush(Color.FromRgb(0x3B, 0x77, 0xE8));
        });

    private void OnReceiverActivity(long count, int x, int y) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsBeingControlled = true;
            UpdatePill();
            Side = "제어당하는 중";
            Detail = $"{count}개 이동 수신 · 마지막 픽셀 ({x}, {y})";
            SideBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
        });

    private static string GetLocalIps()
    {
        try
        {
            var ips = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Where(ip => !ip.StartsWith("169.254."))
                .Distinct().ToList();
            return ips.Count > 0 ? string.Join("   ", ips) : "IP를 찾지 못했습니다";
        }
        catch { return "IP를 찾지 못했습니다"; }
    }
}
