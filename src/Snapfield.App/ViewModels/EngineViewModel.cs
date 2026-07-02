using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Snapfield.App.Mvvm;
using Snapfield.Core.Geometry;
using Snapfield.Core.Model;
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
    // For the single-machine phantom test the engine detects edges in Windows
    // pixel space but routes in physical space, so the two MUST agree on the
    // monitor arrangement. We therefore build the physical layout directly from
    // the Windows pixel arrangement (real EDID sizes, Windows ordering/offsets)
    // instead of the drag-calibrated layout — otherwise a mismatch makes the
    // phantom unreachable. The drag calibration is for the cross-machine phase.
    private DesktopLayout BuildLayout()
    {
        var detected = new MonitorEnumerator().Enumerate();
        var local = BuildWindowsAlignedLayout(detected);
        if (!UsePhantom || local.Count == 0) return new DesktopLayout(local);

        local.Add(MakePhantom(local));
        return new DesktopLayout(local);
    }

    /// <summary>
    /// Places each monitor on the physical plane using its Windows pixel position
    /// (scaled by the primary monitor's density) so ordering and offsets match the
    /// OS, while keeping each monitor's real EDID physical size.
    /// </summary>
    private static List<MonitorInfo> BuildWindowsAlignedLayout(IReadOnlyList<MonitorInfo> detected)
    {
        if (detected.Count == 0) return new List<MonitorInfo>();

        // Primary ≈ the monitor nearest the pixel origin (0,0).
        var primary = detected.OrderBy(m => Math.Abs(m.PixelBounds.Left) + Math.Abs(m.PixelBounds.Top)).First();
        var mmPerPx = primary.PixelsPerMmX > 0 ? 1.0 / primary.PixelsPerMmX : 0.2645;

        return detected.Select(m => m with
        {
            PhysicalBounds = new PhysicalRect(
                m.PixelBounds.Left * mmPerPx,
                m.PixelBounds.Top * mmPerPx,
                m.PhysicalBounds.WidthMm > 0 ? m.PhysicalBounds.WidthMm : m.PixelBounds.Width * mmPerPx,
                m.PhysicalBounds.HeightMm > 0 ? m.PhysicalBounds.HeightMm : m.PixelBounds.Height * mmPerPx),
        }).ToList();
    }

    /// <summary>A virtual 24" 1080p "remote" monitor glued to the physical right of
    /// the right-most monitor (which is the reachable right edge of the desktop).</summary>
    private static MonitorInfo MakePhantom(IReadOnlyList<MonitorInfo> local)
    {
        var anchor = local.OrderByDescending(m => m.PhysicalBounds.Right).First();
        const double wMm = 531.4, hMm = 298.9;
        return new MonitorInfo
        {
            MachineId = "PHANTOM",
            DeviceId = "phantom-24",
            DisplayName = "Phantom 24\" (remote sim)",
            PixelBounds = new PixelRect(0, 0, 1920, 1080),
            PhysicalBounds = new PhysicalRect(
                anchor.PhysicalBounds.Right,
                anchor.PhysicalBounds.Center.YMm - hMm / 2,
                wMm, hMm),
        };
    }
}
