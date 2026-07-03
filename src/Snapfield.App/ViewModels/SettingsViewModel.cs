using System.Windows;
using System.Windows.Input;
using Snapfield.App.Mvvm;
using Snapfield.Core.Persistence;
using Snapfield.Platform.Net;

namespace Snapfield.App.ViewModels;

/// <summary>
/// The Settings tab — consolidates what used to be scattered across the tray menu
/// and the Input Engine window: run-at-login, session restore, cursor speed, the
/// pairing code, and the firewall rule.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly NetworkViewModel _network;

    public SettingsViewModel(NetworkViewModel network)
    {
        _network = network;
        _autoStart = StartupRegistry.IsEnabled;
        _restoreOnLaunch = SettingsStore.Load().RestoreOnLaunch;
        RegisterFirewallCommand = new RelayCommand(RegisterFirewall);
    }

    private bool _autoStart;
    public bool AutoStart
    {
        get => _autoStart;
        set { if (SetField(ref _autoStart, value)) StartupRegistry.Set(value); }
    }

    private bool _restoreOnLaunch;
    public bool RestoreOnLaunch
    {
        get => _restoreOnLaunch;
        set { if (SetField(ref _restoreOnLaunch, value)) SettingsStore.Save(SettingsStore.Load() with { RestoreOnLaunch = value }); }
    }

    /// <summary>Cursor speed on the remote screen — delegates to the live session.</summary>
    public double Sensitivity
    {
        get => _network.Sensitivity;
        set { _network.Sensitivity = value; OnPropertyChanged(); }
    }

    /// <summary>This PC's pairing code (shown so the user can hand it out / verify).</summary>
    public string ReceiverPin => _network.ReceiverPin;

    public string MachineName => _network.MachineName;

    public ICommand RegisterFirewallCommand { get; }

    private string _firewallStatus = "";
    public string FirewallStatus { get => _firewallStatus; private set => SetField(ref _firewallStatus, value); }

    private void RegisterFirewall()
    {
        FirewallStatus = "등록 중… (권한 요청이 뜨면 허용하세요)";
        new Thread(() =>
        {
            var msg = FirewallHelper.EnsureRule();
            Application.Current?.Dispatcher.BeginInvoke(() => FirewallStatus = msg);
        }) { IsBackground = true, Name = "Snapfield.Firewall" }.Start();
    }
}
