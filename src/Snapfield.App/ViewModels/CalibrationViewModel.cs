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

        // Auto re-detect when a monitor is plugged/unplugged — no manual button.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
    }

    private void OnDisplaysChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(ReDetect);

    public void ShutDown() => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;

    /// <summary>Saves silently (no status message) — used for auto-save after a drag.</summary>
    public void AutoSave() => LayoutStore.Save(AppPaths.LayoutFile, new DesktopLayout(Monitors.Select(m => m.ToMonitorInfo())));

    private void OnStoreSaved()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var saved = LayoutStore.Load(AppPaths.LayoutFile);
            if (saved is null) return;

            var detected = new MonitorEnumerator().Enumerate();
            var merged = LayoutStore.Merge(detected, saved);

            // Reload on ANY change to the plane — a peer joining OR the controller
            // moving/resizing a monitor (real-time sync). Skip when the file already
            // matches what's on screen (our own save echo) to avoid needless rebuilds.
            if (MatchesCurrent(merged)) return;
            Populate(merged);
            StatusText = "배치가 업데이트되었습니다.";
        });
    }

    /// <summary>True if the given layout matches the on-screen monitors in identity,
    /// position and size (within rounding) — i.e. nothing actually changed.</summary>
    private bool MatchesCurrent(DesktopLayout layout)
    {
        if (layout.Monitors.Count != Monitors.Count) return false;
        foreach (var m in layout.Monitors)
        {
            var vm = Monitors.FirstOrDefault(x => x.MachineId == m.MachineId && x.DeviceId == m.DeviceId);
            if (vm is null) return false;
            var b = m.PhysicalBounds;
            if (Math.Abs(vm.XMm - b.XMm) > 0.5 || Math.Abs(vm.YMm - b.YMm) > 0.5 ||
                Math.Abs(vm.WidthMm - b.WidthMm) > 0.5 || Math.Abs(vm.HeightMm - b.HeightMm) > 0.5)
                return false;
        }
        return true;
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

    /// <summary>Corrects a monitor's physical size from its true diagonal, deriving
    /// width/height from the pixel aspect ratio (square pixels assumed).</summary>
    public void ResizeMonitor(MonitorViewModel m, double diagonalInches)
    {
        if (diagonalInches is < 5 or > 120)
        {
            StatusText = "5~120인치 범위로 입력하세요.";
            return;
        }
        var diagMm = diagonalInches * 25.4;
        var pixelDiag = Math.Sqrt((double)m.PixelWidth * m.PixelWidth + (double)m.PixelHeight * m.PixelHeight);
        var w = diagMm * m.PixelWidth / pixelDiag;
        var h = diagMm * m.PixelHeight / pixelDiag;
        m.SetPhysicalSize(w, h);
        RecomputeTransform();
        Save(); // persists + applies live to a running session
        StatusText = $"'{m.DisplayName}' 크기를 {diagonalInches:0.#}인치({w:0}×{h:0}mm)로 보정했습니다.";
    }

    /// <summary>Flips a display between laptop and monitor when auto-detection got
    /// it wrong (or a remote arrived from a build without the flag).</summary>
    public void ToggleKind(MonitorViewModel m)
    {
        m.IsLaptop = !m.IsLaptop;
        Save();
        StatusText = $"'{m.DisplayName}'을(를) {m.KindLabel}(으)로 변경했습니다.";
    }

    /// <summary>Removes a REMOTE monitor from the plane (a stale peer). Local
    /// monitors are physically attached and would only re-appear on re-detect.</summary>
    public void RemoveMonitor(MonitorViewModel m)
    {
        if (!m.IsRemote)
        {
            StatusText = "로컬 모니터는 제거할 수 없습니다 (실제 연결된 화면).";
            return;
        }
        Monitors.Remove(m);
        RecomputeTransform();
        Save(); // persist immediately so it stays gone on the next load
        StatusText = $"'{m.MachineId}' 모니터를 평면에서 제거했습니다. 다시 연결하면 다시 나타납니다.";
    }

    private void Populate(DesktopLayout layout)
    {
        Monitors.Clear();
        foreach (var m in layout.Monitors)
            Monitors.Add(new MonitorViewModel(m, this));
        RecomputeTransform();
    }
}
