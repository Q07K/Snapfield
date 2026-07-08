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
        _autoStart = StartupTask.IsEnabled;
        _restoreOnLaunch = SettingsStore.Load().RestoreOnLaunch;
        RegisterFirewallCommand = new RelayCommand(RegisterFirewall);
        CopyPinCommand = new RelayCommand(CopyPin);
        TogglePinCommand = new RelayCommand(() => PinHidden = !PinHidden);
        RegeneratePinCommand = new RelayCommand(RegeneratePin);

        _network.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NetworkViewModel.ReceiverPin))
            {
                OnPropertyChanged(nameof(ReceiverPin));
                OnPropertyChanged(nameof(PinDisplay));
            }
        };
    }

    private bool _autoStart;
    public bool AutoStart
    {
        get => _autoStart;
        set { if (SetField(ref _autoStart, value)) StartupTask.Set(value); }
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

    // ── Pairing-code management ───────────────────────────────────────────────
    public ICommand CopyPinCommand { get; }
    public ICommand TogglePinCommand { get; }
    public ICommand RegeneratePinCommand { get; }

    private bool _pinHidden;
    public bool PinHidden
    {
        get => _pinHidden;
        set { if (SetField(ref _pinHidden, value)) OnPropertyChanged(nameof(PinDisplay)); }
    }

    public string PinDisplay => _pinHidden ? "• • • • • •" : _network.ReceiverPin;

    private string _pinNote = "";
    public string PinNote { get => _pinNote; private set => SetField(ref _pinNote, value); }

    private void CopyPin()
    {
        try { System.Windows.Clipboard.SetText(_network.ReceiverPin); PinNote = "복사됨."; }
        catch { PinNote = "복사하지 못했습니다 — 다시 시도하세요."; }
    }

    private void RegeneratePin()
    {
        var ok = MessageBox.Show(
            "연결 코드를 재발급할까요?\n\n" +
            "새 코드는 다음 연결부터 적용되고, 이미 연결된 기기는 끊기지 않습니다.\n" +
            "다른 PC에 저장된 최근 연결은 새 코드로 다시 연결해야 합니다.",
            "연결 코드 재발급", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        _network.RegenerateReceiverPin();
        PinNote = "재발급됨 — 수신 기기 화면과 이 카드에 새 코드가 표시됩니다.";
    }

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
