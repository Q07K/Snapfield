using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Snapfield.App.Mvvm;
using Snapfield.Platform.Input;
using Snapfield.Platform.Monitors;
using Snapfield.Platform.Net;

namespace Snapfield.App.ViewModels;

/// <summary>Which page of the network window is showing.</summary>
public enum NetMode { Choose, Receiver, Controller }

/// <summary>
/// Drives the network sharing window. Opens on a role-selection page, then
/// shows a dedicated page per role: the receiver page displays this PC's IP and
/// a Listen button; the controller page has an IP box and a Connect button.
/// </summary>
public sealed class NetworkViewModel : ObservableObject
{
    private readonly string _machineId = Environment.MachineName;
    private NetworkSession? _session;

    public ICommand ChooseReceiverCommand { get; }
    public ICommand ChooseControllerCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ListenCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand StopCommand { get; }

    public NetworkViewModel()
    {
        ChooseReceiverCommand = new RelayCommand(() => Mode = NetMode.Receiver);
        ChooseControllerCommand = new RelayCommand(() => Mode = NetMode.Controller);
        BackCommand = new RelayCommand(() => { Stop(); Mode = NetMode.Choose; }, () => true);
        ListenCommand = new RelayCommand(Listen, () => !IsActive);
        ConnectCommand = new RelayCommand(Connect, () => !IsActive && !string.IsNullOrWhiteSpace(RemoteHost));
        StopCommand = new RelayCommand(Stop, () => IsActive);
        MachineName = _machineId;
        LocalIps = GetLocalIps();
    }

    public string MachineName { get; }

    /// <summary>This PC's IPv4 address(es), shown on the receiver page.</summary>
    public string LocalIps { get; }

    // ── Page state ───────────────────────────────────────────────────────────
    private NetMode _mode = NetMode.Choose;
    public NetMode Mode
    {
        get => _mode;
        private set
        {
            if (SetField(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsChoosePage));
                OnPropertyChanged(nameof(IsReceiverPage));
                OnPropertyChanged(nameof(IsControllerPage));
            }
        }
    }
    public bool IsChoosePage => Mode == NetMode.Choose;
    public bool IsReceiverPage => Mode == NetMode.Receiver;
    public bool IsControllerPage => Mode == NetMode.Controller;

    // ── Shared state ─────────────────────────────────────────────────────────
    private bool _isActive;
    public bool IsActive { get => _isActive; private set => SetField(ref _isActive, value); }

    private int _port = 45654;
    public int Port { get => _port; set => SetField(ref _port, value); }

    private string _remoteHost = "";
    public string RemoteHost { get => _remoteHost; set => SetField(ref _remoteHost, value); }

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
        StartSession(s => s.Listen(Port));
    }

    private void Connect()
    {
        StartSession(s => s.Connect(RemoteHost.Trim(), Port));
    }

    private void StartSession(Action<NetworkSession> begin)
    {
        var monitors = new MonitorEnumerator().Enumerate();
        _session = new NetworkSession(_machineId, monitors);
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
        Status = "";
        Side = "—";
        Detail = "";
        SideBrush = Brushes.Gray;
    }

    public void ShutDown() => Stop();

    // ── Session callbacks ────────────────────────────────────────────────────
    private void OnStatus(string s) =>
        Application.Current?.Dispatcher.BeginInvoke(() => Status = s);

    private int _remoteCount;

    private void OnControllerReady(int remoteCount) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _remoteCount = remoteCount;
            if (remoteCount == 0)
                Status = "경고: 상대 PC가 모니터 0개를 보냈습니다 — 커서가 넘어갈 수 없습니다. " +
                         "상대 PC의 Snapfield가 최신 버전인지 확인하세요.";
        });

    private void OnEngineStatus(EngineStatus s) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var remote = s.Captured || s.ActiveIsRemote;
            Side = remote ? "REMOTE — 상대 PC 제어 중" : "LOCAL";
            Detail = $"{s.ActiveMonitor}   ({s.VirtualXMm:0} mm, {s.VirtualYMm:0} mm)   ·   상대 모니터 {_remoteCount}개";
            SideBrush = remote
                ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x2A))
                : new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
        });

    private void OnReceiverActivity(long count, int x, int y) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Side = "RECEIVING — 제어당하는 중";
            Detail = $"{count}개 이동 수신 · 마지막 픽셀 ({x}, {y})";
            SideBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
        });

    // ── Helpers ──────────────────────────────────────────────────────────────
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
                .Where(ip => !ip.StartsWith("169.254.")) // APIPA
                .Distinct()
                .ToList();
            return ips.Count > 0 ? string.Join("   ", ips) : "IP를 찾지 못했습니다";
        }
        catch { return "IP를 찾지 못했습니다"; }
    }
}
