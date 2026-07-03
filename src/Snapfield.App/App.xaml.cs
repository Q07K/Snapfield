using System.Windows;
using Snapfield.App.ViewModels;
using Snapfield.Core.Persistence;
using Snapfield.Platform.Monitors;

namespace Snapfield.App;

/// <summary>
/// Tray-resident application with a single main window (three tabs: 연결 · 배치 ·
/// 설정). The network session lives at app level and keeps running when the
/// window is closed to the tray. Exit is via the tray menu only.
/// </summary>
public partial class App : Application
{
    private TrayManager? _tray;
    private MainWindow? _main;

    /// <summary>App-wide network session; survives window close.</summary>
    public NetworkViewModel Network { get; private set; } = null!;

    public bool IsExiting { get; private set; }

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        MonitorEnumerator.EnableDpiAwareness();
        base.OnStartup(e);

        Network = new NetworkViewModel();
        _tray = new TrayManager(this);

        ShowMain(MainTab.Connect);

        if (SettingsStore.Load().RestoreOnLaunch)
            Network.RestoreLastSession();
    }

    /// <summary>Shows the main window and optionally jumps to a tab.</summary>
    public void ShowMain(MainTab? tab = null)
    {
        if (_main is null)
        {
            _main = new MainWindow { DataContext = new MainViewModel(Network) };
            _main.Closed += (_, _) => _main = null;
        }
        if (tab is { } t && _main.DataContext is MainViewModel vm) vm.Tab = t;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void NotifyHiddenToTray() => _tray?.ShowHideHint();

    public void ExitFromTray()
    {
        IsExiting = true;
        (_main?.DataContext as MainViewModel)?.ShutDown();
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
