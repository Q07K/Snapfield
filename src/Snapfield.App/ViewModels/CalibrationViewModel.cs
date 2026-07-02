using System.Collections.ObjectModel;
using System.Windows.Input;
using Snapfield.App.Mvvm;
using Snapfield.Core.Model;
using Snapfield.Core.Persistence;
using Snapfield.Platform.Monitors;

namespace Snapfield.App.ViewModels;

/// <summary>
/// Drives the calibration canvas: owns the monitor view models, the mm↔canvas
/// transform, edge snapping, and load/save of the layout.
/// </summary>
public sealed class CalibrationViewModel : ObservableObject
{
    private const double MarginFraction = 0.12; // breathing room around the plane
    private const double SnapPixels = 10.0;      // edge-snap radius, in canvas pixels

    public ObservableCollection<MonitorViewModel> Monitors { get; } = new();

    public ICommand ReDetectCommand { get; }
    public ICommand AutoArrangeCommand { get; }
    public ICommand SaveCommand { get; }

    public CalibrationViewModel()
    {
        ReDetectCommand = new RelayCommand(ReDetect);
        AutoArrangeCommand = new RelayCommand(AutoArrange);
        SaveCommand = new RelayCommand(Save);
        Load();

        // A network session persists the peer's monitors into the layout file on
        // connect — refresh the canvas when new monitors appear on the plane.
        LayoutStore.Saved += OnStoreSaved;
    }

    private void OnStoreSaved()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var saved = LayoutStore.Load(AppPaths.LayoutFile);
            if (saved is null) return;

            // Only reload when the SET of monitors changed (e.g. a remote peer
            // appeared) — never clobber in-progress drag edits for a plain save.
            var fileKeys = saved.Monitors.Select(m => m.Key).ToHashSet();
            var vmKeys = Monitors.Select(m => $"{m.MachineId}/{m.DeviceId}").ToHashSet();
            if (fileKeys.SetEquals(vmKeys)) return;

            var detected = new MonitorEnumerator().Enumerate();
            Populate(LayoutStore.Merge(detected, saved));
            StatusText = "Remote monitors joined the plane — drag them into place and Save.";
        });
    }

    // ── Transform state ──────────────────────────────────────────────────────
    private double _scale = 1.0;
    public double Scale { get => _scale; private set => SetField(ref _scale, value); }

    private double _offsetX, _offsetY, _planeMinXMm, _planeMinYMm;
    private double _viewportW, _viewportH;

    public double MmToCanvasX(double xMm) => (xMm - _planeMinXMm) * Scale + _offsetX;
    public double MmToCanvasY(double yMm) => (yMm - _planeMinYMm) * Scale + _offsetY;
    public double CanvasXToMm(double cx) => (cx - _offsetX) / Scale + _planeMinXMm;
    public double CanvasYToMm(double cy) => (cy - _offsetY) / Scale + _planeMinYMm;

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }

    /// <summary>Recompute the fit-to-view transform for a new viewport size.</summary>
    public void UpdateViewport(double width, double height)
    {
        _viewportW = width;
        _viewportH = height;
        RecomputeTransform();
    }

    private void RecomputeTransform()
    {
        if (Monitors.Count == 0 || _viewportW <= 0 || _viewportH <= 0) return;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var m in Monitors)
        {
            minX = Math.Min(minX, m.XMm);
            minY = Math.Min(minY, m.YMm);
            maxX = Math.Max(maxX, m.XMm + m.WidthMm);
            maxY = Math.Max(maxY, m.YMm + m.HeightMm);
        }
        var planeW = Math.Max(maxX - minX, 1);
        var planeH = Math.Max(maxY - minY, 1);

        var usableW = _viewportW * (1 - 2 * MarginFraction);
        var usableH = _viewportH * (1 - 2 * MarginFraction);
        Scale = Math.Min(usableW / planeW, usableH / planeH);

        _planeMinXMm = minX;
        _planeMinYMm = minY;
        // Centre the plane in the viewport.
        _offsetX = (_viewportW - planeW * Scale) / 2;
        _offsetY = (_viewportH - planeH * Scale) / 2;

        foreach (var m in Monitors) m.RefreshCanvas();
    }

    // ── Edge snapping ────────────────────────────────────────────────────────
    /// <summary>
    /// Snaps the moving monitor's edges to nearby neighbour edges (left↔right,
    /// top↔bottom, and same-side alignment) within a pixel-based radius.
    /// </summary>
    public (double X, double Y) SnapEdges(MonitorViewModel moving, double xMm, double yMm)
    {
        var snapMm = SnapPixels / Math.Max(Scale, 1e-6);
        double W = moving.WidthMm, H = moving.HeightMm;
        double left = xMm, right = xMm + W, top = yMm, bottom = yMm + H;

        // For each axis, keep the candidate position whose edge sits closest to a
        // neighbour edge, as long as it is within the snap radius.
        double bestDx = snapMm, bestDy = snapMm;
        double resultX = xMm, resultY = yMm;

        void ConsiderX(double edge, double target, double candidateX)
        {
            var d = Math.Abs(edge - target);
            if (d < bestDx) { bestDx = d; resultX = candidateX; }
        }
        void ConsiderY(double edge, double target, double candidateY)
        {
            var d = Math.Abs(edge - target);
            if (d < bestDy) { bestDy = d; resultY = candidateY; }
        }

        foreach (var o in Monitors)
        {
            if (ReferenceEquals(o, moving)) continue;
            double oLeft = o.XMm, oRight = o.XMm + o.WidthMm, oTop = o.YMm, oBottom = o.YMm + o.HeightMm;

            ConsiderX(left, oRight, oRight);          // moving.left  butts neighbour.right
            ConsiderX(right, oLeft, oLeft - W);       // moving.right butts neighbour.left
            ConsiderX(left, oLeft, oLeft);            // align left edges
            ConsiderX(right, oRight, oRight - W);      // align right edges

            ConsiderY(top, oBottom, oBottom);         // moving.top   butts neighbour.bottom
            ConsiderY(bottom, oTop, oTop - H);        // moving.bottom butts neighbour.top
            ConsiderY(top, oTop, oTop);               // align top edges
            ConsiderY(bottom, oBottom, oBottom - H);   // align bottom edges
        }
        return (resultX, resultY);
    }

    // ── Data operations ──────────────────────────────────────────────────────
    private void Load()
    {
        var detected = new MonitorEnumerator().Enumerate();
        var saved = LayoutStore.Load(AppPaths.LayoutFile);
        var layout = LayoutStore.Merge(detected, saved);
        Populate(layout);
        StatusText = saved is null
            ? $"{layout.Monitors.Count} monitor(s) detected — no saved layout, using provisional placement."
            : $"{layout.Monitors.Count} monitor(s) — restored saved calibration.";
    }

    private void ReDetect()
    {
        var detected = new MonitorEnumerator().Enumerate();
        var current = new DesktopLayout(Monitors.Select(m => m.ToMonitorInfo()));
        var layout = LayoutStore.Merge(detected, current); // keep current placement for known monitors
        Populate(layout);
        StatusText = $"Re-detected: {layout.Monitors.Count} monitor(s).";
    }

    private void AutoArrange()
    {
        // Re-seed left-to-right in pixel order, top-aligned, no gaps.
        var ordered = Monitors.OrderBy(m => m.PixelLeft).ToList();
        double cursorX = 0;
        foreach (var m in ordered)
        {
            m.DragTo(cursorX, 0);
            cursorX += m.WidthMm;
        }
        RecomputeTransform();
        StatusText = "Auto-arranged left-to-right (top-aligned).";
    }

    private void Save()
    {
        var layout = new DesktopLayout(Monitors.Select(m => m.ToMonitorInfo()));
        LayoutStore.Save(AppPaths.LayoutFile, layout);
        StatusText = $"Saved to {AppPaths.LayoutFile}";
    }

    private void Populate(DesktopLayout layout)
    {
        Monitors.Clear();
        foreach (var m in layout.Monitors)
            Monitors.Add(new MonitorViewModel(m, this));
        RecomputeTransform();
    }
}
