using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Snapfield.App.Mvvm;
using Snapfield.Platform.Input;
using Snapfield.Platform.Monitors;
using Snapfield.Platform.Net;

namespace Snapfield.App.ViewModels;

/// <summary>
/// Drives the network sharing panel: this machine can either LISTEN (become the
/// controllable receiver) or CONNECT to another machine (become the controller).
/// </summary>
public sealed class NetworkViewModel : ObservableObject
{
    private readonly string _machineId = Environment.MachineName;
    private NetworkSession? _session;

    public ICommand ListenCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand StopCommand { get; }

    public NetworkViewModel()
    {
        ListenCommand = new RelayCommand(Listen, () => !IsActive);
        ConnectCommand = new RelayCommand(Connect, () => !IsActive && !string.IsNullOrWhiteSpace(RemoteHost));
        StopCommand = new RelayCommand(Stop, () => IsActive);
        MachineName = _machineId;
    }

    public string MachineName { get; }

    private bool _isActive;
    public bool IsActive { get => _isActive; private set { if (SetField(ref _isActive, value)) OnPropertyChanged(nameof(RoleLabel)); } }

    private string _role = "Not connected";
    public string RoleLabel { get => IsActive ? _role : "Not connected"; }

    private int _port = 45654;
    public int Port { get => _port; set => SetField(ref _port, value); }

    private string _remoteHost = "";
    public string RemoteHost { get => _remoteHost; set => SetField(ref _remoteHost, value); }

    private string _status = "Idle. Listen on this PC, or connect to the other PC's IP.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    // Live control-side indicator (controller only).
    private string _side = "—";
    public string Side { get => _side; private set => SetField(ref _side, value); }

    private Brush _sideBrush = Brushes.Gray;
    public Brush SideBrush { get => _sideBrush; private set => SetField(ref _sideBrush, value); }

    private string _detail = "";
    public string Detail { get => _detail; private set => SetField(ref _detail, value); }

    private void Listen()
    {
        _role = "Receiver (this PC can be controlled)";
        StartSession(s => s.Listen(Port));
    }

    private void Connect()
    {
        _role = $"Controller (driving {RemoteHost})";
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
    }

    private void Stop()
    {
        _session?.Dispose();
        _session = null;
        IsActive = false;
        Status = "Stopped.";
        Side = "—";
        Detail = "";
        SideBrush = Brushes.Gray;
    }

    public void ShutDown() => Stop();

    private void OnStatus(string s) =>
        Application.Current?.Dispatcher.BeginInvoke(() => Status = s);

    private int _remoteCount;

    private void OnEngineStatus(EngineStatus s) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var remote = s.Captured || s.ActiveIsRemote;
            Side = remote ? "REMOTE — controlling other PC" : "LOCAL";
            Detail = $"{s.ActiveMonitor}   ({s.VirtualXMm:0} mm, {s.VirtualYMm:0} mm)   ·   remote monitors: {_remoteCount}";
            SideBrush = remote
                ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x2A))
                : new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
        });

    private void OnControllerReady(int remoteCount) =>
        Application.Current?.Dispatcher.BeginInvoke(() => _remoteCount = remoteCount);

    // Receiver side: prove packets are arriving by lighting up on each injected move.
    private void OnReceiverActivity(long count, int x, int y) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Side = "RECEIVING — being controlled";
            Detail = $"{count} moves · last pixel ({x}, {y})";
            SideBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
        });
}
