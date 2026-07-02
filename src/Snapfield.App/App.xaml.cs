using System.Windows;
using Snapfield.App.ViewModels;
using Snapfield.Core.Persistence;
using Snapfield.Platform.Monitors;

namespace Snapfield.App;

/// <summary>
/// Tray-resident application. Windows are transient views; the network session
/// (NetworkViewModel) lives at app level and keeps running when every window is
/// closed. Exit happens only via the tray menu.
/// </summary>
public partial class App : Application
{
    private TrayManager? _tray;
    private MainWindow? _main;
    private NetworkWindow? _network;

    /// <summary>App-wide network session; survives window close.</summary>
    public NetworkViewModel Network { get; private set; } = null!;

    /// <summary>True while the tray menu is tearing the app down.</summary>
    public bool IsExiting { get; private set; }

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Belt-and-braces: the manifest already sets PerMonitorV2 (the call fails
        // once WPF owns an HWND, which is why the manifest is the real fix).
        MonitorEnumerator.EnableDpiAwareness();
        base.OnStartup(e);

        Network = new NetworkViewModel();
        _tray = new TrayManager(this);

        ShowMain();

        // Resume the last session (receiver re-listens / controller re-connects);
        // combined with auto-reconnect this makes the pair self-healing on boot.
        if (SettingsStore.Load().RestoreOnLaunch)
            Network.RestoreLastSession();
    }

    public void ShowMain()
    {
        if (_main is null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) => _main = null;
        }
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void ShowNetwork()
    {
        if (_network is null)
        {
            _network = new NetworkWindow();
            _network.Closed += (_, _) => _network = null;
        }
        _network.Show();
        _network.Activate();
    }

    public void NotifyHiddenToTray() => _tray?.ShowHideHint();

    public void ExitFromTray()
    {
        IsExiting = true;
        Network.ShutDown();
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
