using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Snapfield.App.Mvvm;
using Snapfield.Core.Geometry;
using Snapfield.Core.Model;
using Snapfield.Core.Persistence;
using Snapfield.Platform.Input;
using Snapfield.Platform.Monitors;

namespace Snapfield.App.ViewModels;

/// <summary>
/// Drives the input-engine control panel: start/stop the hook, toggle a phantom
/// remote monitor for single-machine testing, and show live routing state.
/// </summary>
public sealed class EngineViewModel : ObservableObject
{
    private readonly string _machineId = Environment.MachineName;
    private InputEngine? _engine;

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }

    public EngineViewModel()
    {
        StartCommand = new RelayCommand(Start, () => !IsRunning);
        StopCommand = new RelayCommand(Stop, () => IsRunning);
    }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; private set { if (SetField(ref _isRunning, value)) OnPropertyChanged(nameof(RunLabel)); } }
    public string RunLabel => IsRunning ? "Engine: RUNNING" : "Engine: stopped";

    private bool _usePhantom = true;
    public bool UsePhantom
    {
        get => _usePhantom;
        set { if (SetField(ref _usePhantom, value) && IsRunning) _engine?.UpdateLayout(BuildLayout()); }
    }

    private double _sensitivity = 1.0;
    public double Sensitivity
    {
        get => _sensitivity;
        set { if (SetField(ref _sensitivity, value) && _engine is not null) _engine.Sensitivity = value; }
    }

    // ── Live status ──────────────────────────────────────────────────────────
    private string _side = "—";
    public string Side { get => _side; private set => SetField(ref _side, value); }

    private string _activeMonitor = "—";
    public string ActiveMonitor { get => _activeMonitor; private set => SetField(ref _activeMonitor, value); }

    private string _virtualPos = "—";
    public string VirtualPos { get => _virtualPos; private set => SetField(ref _virtualPos, value); }

    private Brush _sideBrush = Brushes.Gray;
    public Brush SideBrush { get => _sideBrush; private set => SetField(ref _sideBrush, value); }

    private string _hint = "Enable the engine, then push the cursor off the right edge to cross into the phantom screen.";
    public string Hint { get => _hint; private set => SetField(ref _hint, value); }

    // ── Engine lifecycle ─────────────────────────────────────────────────────
    private void Start()
    {
        _engine = new InputEngine(_machineId, BuildLayout()) { Sensitivity = Sensitivity };
        _engine.StatusChanged += OnStatusChanged;
        _engine.Start();
        IsRunning = true;
    }

    private void Stop()
    {
        if (_engine is not null)
        {
            _engine.StatusChanged -= OnStatusChanged;
            _engine.Dispose();
            _engine = null;
        }
        IsRunning = false;
        Side = "—";
        ActiveMonitor = "—";
        VirtualPos = "—";
        SideBrush = Brushes.Gray;
    }

    public void ShutDown() => Stop();

    private void OnStatusChanged(EngineStatus s)
    {
        // Marshal from the hook thread to the UI thread.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Side = s.Captured || s.ActiveIsRemote ? "REMOTE (captured)" : "LOCAL";
            ActiveMonitor = s.ActiveMonitor;
            VirtualPos = $"{s.VirtualXMm:0} mm, {s.VirtualYMm:0} mm";
            SideBrush = s.Captured || s.ActiveIsRemote
                ? new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x2A))   // orange = on remote
                : new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));  // green = local
            Hint = s.Captured
                ? "On the phantom screen — cursor parked locally. Move left to return."
                : "Local control. Push off the right edge to cross into the phantom.";
        });
    }

    // ── Layout construction ──────────────────────────────────────────────────
    private DesktopLayout BuildLayout()
    {
        var detected = new MonitorEnumerator().Enumerate();
        var saved = LayoutStore.Load(AppPaths.LayoutFile);
        var layout = LayoutStore.Merge(detected, saved);
        if (!UsePhantom) return layout;

        var monitors = layout.Monitors.ToList();
        monitors.Add(MakePhantom(layout));
        return new DesktopLayout(monitors);
    }

    /// <summary>A virtual 24" 1080p "remote" monitor placed just right of the real layout.</summary>
    private static MonitorInfo MakePhantom(DesktopLayout layout)
    {
        double rightMm = 0, midY = 150;
        if (layout.Monitors.Count > 0)
        {
            rightMm = layout.Monitors.Max(m => m.PhysicalBounds.Right);
            var b = layout.Bounds;
            midY = b.YMm + b.HeightMm / 2;
        }
        const double wMm = 531.4, hMm = 298.9;
        return new MonitorInfo
        {
            MachineId = "PHANTOM",
            DeviceId = "phantom-24",
            DisplayName = "Phantom 24\" (remote sim)",
            PixelBounds = new PixelRect(0, 0, 1920, 1080),
            PhysicalBounds = new PhysicalRect(rightMm, midY - hMm / 2, wMm, hMm),
        };
    }
}
