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
///   • Choose  — a toggle between 조작 기기 (controller) and 수신 기기 (receiver)
///   • Controller — a list of recent machines + a form to add a new one
///   • Receiver   — this PC's IP + pairing code, with a live "waiting / controlled" state
/// </summary>
public sealed class NetworkViewModel : ObservableObject
{
    private readonly string _machineId = Environment.MachineName;
    private NetworkSession? _session;

    public NetworkViewModel()
    {
        SelectControllerCommand = new RelayCommand(() => IsControllerSelected = true);
        SelectReceiverCommand = new RelayCommand(() => IsControllerSelected = false);
        ProceedCommand = new RelayCommand(() => Mode = IsControllerSelected ? NetMode.Controller : NetMode.Receiver);
        BackCommand = new RelayCommand(() => { Stop(); ShowNewForm = false; Mode = NetMode.Choose; });
        ListenCommand = new RelayCommand(Listen, () => !IsActive);
        ConnectCommand = new RelayCommand(Connect, () => !IsActive && !string.IsNullOrWhiteSpace(RemoteHost) && !string.IsNullOrWhiteSpace(ControllerPin));
        ConnectRecentCommand = new RelayCommand<RecentConnection>(ConnectRecent, r => r is not null && !IsActive);
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

    public ICommand SelectControllerCommand { get; }
    public ICommand SelectReceiverCommand { get; }
    public ICommand ProceedCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ListenCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand ConnectRecentCommand { get; }
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

    // ── Choose page (B3 toggle) ───────────────────────────────────────────────
    private bool _isControllerSelected = true;
    public bool IsControllerSelected
    {
        get => _isControllerSelected;
        set { if (SetField(ref _isControllerSelected, value)) { OnPropertyChanged(nameof(IsReceiverSelected)); OnPropertyChanged(nameof(RoleDesc)); } }
    }
    public bool IsReceiverSelected => !_isControllerSelected;
    public string RoleDesc => IsControllerSelected
        ? "이 PC의 마우스·키보드로 다른 PC를 조작합니다."
        : "다른 PC의 마우스·키보드를 이 PC로 받습니다.";

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
        private set { if (SetField(ref _isActive, value)) { OnPropertyChanged(nameof(IsIdle)); OnPropertyChanged(nameof(IsWaiting)); } }
    }
    public bool IsIdle => !_isActive;

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
    public string ControllerPin { get => _controllerPin; set => SetField(ref _controllerPin, value); }

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

        StartSession(s => s.Listen(Port));
        SettingsStore.Save(SettingsStore.Load() with { LastRole = "Receiver", LastPort = Port });
    }

    private void Connect()
    {
        var host = RemoteHost.Trim();
        var pin = ControllerPin.Trim();
        ShowNewForm = false; // close the sheet as we connect
        StartSession(s => s.Connect(host, Port));
        SettingsStore.Save(SettingsStore.Load() with { LastRole = "Controller", LastHost = host, LastPort = Port, ControllerPin = pin });
        Remember(host, Port, pin, host); // name refined once the peer says hello
    }

    private void ConnectRecent(RecentConnection? r)
    {
        if (r is null) return;
        RemoteHost = r.Host;
        Port = r.Port;
        ControllerPin = r.Pin;
        Connect();
    }

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

    private void StartSession(Action<NetworkSession> begin)
    {
        var monitors = new MonitorEnumerator().Enumerate();
        _session = new NetworkSession(_machineId, monitors)
        {
            Sensitivity = Sensitivity,
            ReceiverPin = ReceiverPin,
            ControllerPin = ControllerPin.Trim(),
        };
        _session.Status += OnStatus;
        _session.EngineStatus += OnEngineStatus;
        _session.ControllerReady += OnControllerReady;
        _session.ReceiverActivity += OnReceiverActivity;
        begin(_session);
        IsActive = true;
        if (monitors.Count == 0)
            Status = "경고: 이 PC의 모니터를 하나도 인식하지 못했습니다!";
    }

    private void Stop()
    {
        _session?.Dispose();
        _session = null;
        IsActive = false;
        IsBeingControlled = false;
        Status = "";
        Side = "—";
        Detail = "";
        SideBrush = Brushes.Gray;
    }

    public void ShutDown() => Stop();

    // ── Session callbacks ─────────────────────────────────────────────────────
    private void OnStatus(string s) =>
        Application.Current?.Dispatcher.BeginInvoke(() => Status = s);

    private int _remoteCount;

    private void OnControllerReady(int remoteCount) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _remoteCount = remoteCount;
            // Refine the recent entry's name now that the peer identified itself.
            if (_session is { RemoteMachineId.Length: > 0 } && !string.IsNullOrWhiteSpace(RemoteHost))
                Remember(RemoteHost.Trim(), Port, ControllerPin.Trim(), _session.RemoteMachineId);
            if (remoteCount == 0)
                Status = "경고: 상대 PC가 모니터 0개를 보냈습니다 — 상대 Snapfield가 최신인지 확인하세요.";
        });

    private void OnEngineStatus(EngineStatus s) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var remote = s.Captured || s.ActiveIsRemote;
            Side = remote ? "상대 PC 조작 중" : "이 PC (대기)";
            Detail = $"{s.ActiveMonitor}   ({s.VirtualXMm:0}, {s.VirtualYMm:0} mm)   ·   상대 모니터 {_remoteCount}개";
            SideBrush = remote
                ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x2A))
                : new SolidColorBrush(Color.FromRgb(0x3B, 0x77, 0xE8));
        });

    private void OnReceiverActivity(long count, int x, int y) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsBeingControlled = true;
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
