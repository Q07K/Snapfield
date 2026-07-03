using System.Windows.Input;
using Snapfield.App.Mvvm;

namespace Snapfield.App.ViewModels;

public enum MainTab { Connect, Calibrate, Settings }

/// <summary>
/// Shell view model for the single main window. Hosts the three tabs — Connect
/// (network), Calibrate (monitor layout), and Settings — and tracks which is
/// active. The old Input Engine window is gone: the engine runs automatically
/// when a connection is active.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    public MainViewModel(NetworkViewModel network)
    {
        Network = network;
        Calibration = new CalibrationViewModel();
        Settings = new SettingsViewModel(network);

        SelectConnectCommand = new RelayCommand(() => Tab = MainTab.Connect);
        SelectCalibrateCommand = new RelayCommand(() => Tab = MainTab.Calibrate);
        SelectSettingsCommand = new RelayCommand(() => Tab = MainTab.Settings);
    }

    public NetworkViewModel Network { get; }
    public CalibrationViewModel Calibration { get; }
    public SettingsViewModel Settings { get; }

    public ICommand SelectConnectCommand { get; }
    public ICommand SelectCalibrateCommand { get; }
    public ICommand SelectSettingsCommand { get; }

    private MainTab _tab = MainTab.Connect;
    public MainTab Tab
    {
        get => _tab;
        set
        {
            if (!SetField(ref _tab, value)) return;
            OnPropertyChanged(nameof(IsConnect));
            OnPropertyChanged(nameof(IsCalibrate));
            OnPropertyChanged(nameof(IsSettings));
        }
    }
    public bool IsConnect => _tab == MainTab.Connect;
    public bool IsCalibrate => _tab == MainTab.Calibrate;
    public bool IsSettings => _tab == MainTab.Settings;

    public void ShutDown() => Calibration.ShutDown();
}
