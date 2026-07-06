using System.Collections.ObjectModel;
using System.Windows.Input;
using Snapfield.App.Mvvm;
using Snapfield.Core.Input;
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

    private readonly string _local = Environment.MachineName;
    // Remote machines currently connected. Their monitors show on the plane;
    // when they disconnect they're hidden (placement stays saved for reconnect).
    private HashSet<string> _activeRemotes = new();

    // The controller owns the layout. On a receiver the plane is read-only (it
    // just mirrors what the controller sends) to avoid two-way edit conflicts.
    private bool _isEditable = true;
    public bool IsEditable { get => _isEditable; private set { if (SetField(ref _isEditable, value)) OnPropertyChanged(nameof(IsReadOnly)); } }
    public bool IsReadOnly => !_isEditable;

    public void SetEditable(bool editable)
    {
        if (_isEditable == editable) return;
        IsEditable = editable;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Populate(BuildDisplay()));
    }

    public ICommand ReDetectCommand { get; }
    public ICommand AutoArrangeCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ClearSelectionCommand { get; }

    public CalibrationViewModel()
    {
        ReDetectCommand = new RelayCommand(ReDetect);
        AutoArrangeCommand = new RelayCommand(AutoArrange);
        SaveCommand = new RelayCommand(Save);
        ClearSelectionCommand = new RelayCommand(() => Select(null));
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
    public void AutoSave() { if (_isEditable) PersistCurrent(); }

    private void OnStoreSaved()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var display = BuildDisplay();
            // Reload on ANY change to the plane — a peer joining/leaving OR the
            // controller moving/resizing. Skip when the file already matches what's
            // on screen (our own save echo) to avoid needless rebuilds.
            if (MatchesCurrent(display)) return;
            Populate(display);
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

    // ── Selection (drives the inspector panel) ───────────────────────────────
    private MonitorViewModel? _selected;
    public MonitorViewModel? SelectedMonitor
    {
        get => _selected;
        private set { if (SetField(ref _selected, value)) OnPropertyChanged(nameof(HasSelection)); }
    }
    public bool HasSelection => _selected is not null;

    /// <summary>Selects a monitor (null = clear). Works on read-only planes too —
    /// the inspector shows info there, just with the actions disabled.</summary>
    public void Select(MonitorViewModel? monitor)
    {
        foreach (var m in Monitors) m.IsSelected = ReferenceEquals(m, monitor);
        SelectedMonitor = monitor;
    }

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
        RefreshSeams(force: true);
    }

    // ── Seam flow markers ────────────────────────────────────────────────────
    // Where the cursor crosses between machines: flow arrows on direct bands,
    // ✕ marks where the neighbour has no facing edge (entry gets clamped there).

    /// <summary>One marker on the canvas (top-left position, pre-centred).</summary>
    public sealed class SeamMarker
    {
        public double X { get; init; }
        public double Y { get; init; }
        public double Angle { get; init; } // 0 = horizontal flow, 90 = vertical
    }

    private IReadOnlyList<SeamMarker> _arrowMarkers = Array.Empty<SeamMarker>();
    public IReadOnlyList<SeamMarker> ArrowMarkers { get => _arrowMarkers; private set => SetField(ref _arrowMarkers, value); }

    private IReadOnlyList<SeamMarker> _blockMarkers = Array.Empty<SeamMarker>();
    public IReadOnlyList<SeamMarker> BlockMarkers { get => _blockMarkers; private set => SetField(ref _blockMarkers, value); }

    private const double MarkerSpacingPx = 34;  // one marker per this many canvas pixels
    private const double MinSegmentPx = 10;     // don't mark slivers
    private long _lastSeamTick;

    /// <summary>Recompute the seam markers. Cheap, but throttled anyway because it
    /// runs on every mouse-move during a drag; the drag-release recompute forces.</summary>
    internal void RefreshSeams(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastSeamTick < 80) return;
        _lastSeamTick = now;

        var rects = new List<SeamRect>(Monitors.Count);
        foreach (var m in Monitors)
            rects.Add(new SeamRect(m.MachineId, m.XMm, m.YMm, m.WidthMm, m.HeightMm));

        var arrows = new List<SeamMarker>();
        var blocks = new List<SeamMarker>();
        foreach (var s in SeamScanner.Scan(rects))
        {
            var from = s.Vertical ? MmToCanvasY(s.StartMm) : MmToCanvasX(s.StartMm);
            var to = s.Vertical ? MmToCanvasY(s.EndMm) : MmToCanvasX(s.EndMm);
            var seam = s.Vertical ? MmToCanvasX(s.SeamMm) : MmToCanvasY(s.SeamMm);
            var len = to - from;
            if (len < MinSegmentPx) continue;

            var count = Math.Max(1, (int)(len / MarkerSpacingPx));
            for (var i = 0; i < count; i++)
            {
                var along = from + len * (i + 0.5) / count;
                var (x, y) = s.Vertical ? (seam, along) : (along, seam);
                (s.Direct ? arrows : blocks).Add(new SeamMarker
                {
                    X = x - 10, // centre the ~20×16 marker glyph on the seam point
                    Y = y - 8,
                    Angle = s.Vertical ? 0 : 90,
                });
            }
        }
        ArrowMarkers = arrows;
        BlockMarkers = blocks;
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

    /// <summary>Sets which remote machines are connected; hides the rest and refreshes.</summary>
    public void SetActiveRemotes(IEnumerable<string> machineIds)
    {
        _activeRemotes = machineIds.ToHashSet();
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Populate(BuildDisplay()));
    }

    /// <summary>The layout to show: local monitors + connected remotes only.
    /// Offline remotes stay in the file (placement remembered) but aren't drawn.</summary>
    private DesktopLayout BuildDisplay()
    {
        var detected = new MonitorEnumerator().Enumerate();
        var saved = LayoutStore.Load(AppPaths.LayoutFile);
        var merged = saved is null ? new DesktopLayout(detected) : LayoutStore.Merge(detected, saved);
        // Receiver (read-only): mirror the whole plane the controller sends, so every
        // machine shows the same picture. Controller: show local + connected remotes,
        // hiding offline machines (their placement stays saved for reconnect).
        var shown = _isEditable
            ? merged.Monitors.Where(m => m.MachineId == _local || _activeRemotes.Contains(m.MachineId))
            : merged.Monitors;
        return new DesktopLayout(shown);
    }

    private void Load()
    {
        Populate(BuildDisplay());
        StatusText = $"{Monitors.Count}개 화면 — 저장된 배치를 불러왔습니다.";
    }

    private void ReDetect()
    {
        Populate(BuildDisplay());
        StatusText = $"재감지: {Monitors.Count}개 화면.";
    }

    private void AutoArrange()
    {
        if (!_isEditable) { StatusText = "배치는 조작 기기에서만 변경할 수 있습니다."; return; }
        // Re-seed left-to-right in pixel order, top-aligned, no gaps.
        var ordered = Monitors.OrderBy(m => m.PixelLeft).ToList();
        double cursorX = 0;
        foreach (var m in ordered)
        {
            m.DragTo(cursorX, 0);
            cursorX += m.WidthMm;
        }
        RecomputeTransform();
        PersistCurrent();
        StatusText = "자동 정렬 완료 (좌→우, 상단 정렬).";
    }

    /// <summary>Writes the shown monitors, preserving offline machines already in the
    /// file (their placement is remembered), except an explicitly removed key.</summary>
    private void PersistCurrent(string? excludeKey = null)
    {
        var shown = Monitors.Select(m => m.ToMonitorInfo()).ToList();
        var shownKeys = shown.Select(k => k.Key).ToHashSet();
        var saved = LayoutStore.Load(AppPaths.LayoutFile);
        var preserved = saved?.Monitors.Where(m => !shownKeys.Contains(m.Key) && m.Key != excludeKey)
                        ?? Enumerable.Empty<MonitorInfo>();
        LayoutStore.Save(AppPaths.LayoutFile, new DesktopLayout(shown.Concat(preserved)));
    }

    private void Save()
    {
        PersistCurrent();
        StatusText = "배치를 저장했습니다.";
    }

    /// <summary>Corrects a monitor's physical size from its true diagonal, deriving
    /// width/height from the pixel aspect ratio (square pixels assumed).</summary>
    public void ResizeMonitor(MonitorViewModel m, double diagonalInches)
    {
        if (!_isEditable) { StatusText = "배치는 조작 기기에서만 변경할 수 있습니다."; return; }
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
        if (!_isEditable) { StatusText = "배치는 조작 기기에서만 변경할 수 있습니다."; return; }
        m.IsLaptop = !m.IsLaptop;
        Save();
        StatusText = $"'{m.DisplayName}'을(를) {m.KindLabel}(으)로 변경했습니다.";
    }

    /// <summary>Removes a REMOTE monitor from the plane (a stale peer). Local
    /// monitors are physically attached and would only re-appear on re-detect.</summary>
    public void RemoveMonitor(MonitorViewModel m)
    {
        if (!_isEditable) { StatusText = "배치는 조작 기기에서만 변경할 수 있습니다."; return; }
        if (!m.IsRemote)
        {
            StatusText = "로컬 모니터는 제거할 수 없습니다 (실제 연결된 화면).";
            return;
        }
        if (ReferenceEquals(_selected, m)) Select(null);
        Monitors.Remove(m);
        RecomputeTransform();
        PersistCurrent(excludeKey: $"{m.MachineId}/{m.DeviceId}"); // drop from file too
        StatusText = $"'{m.MachineId}' 모니터를 평면에서 제거했습니다.";
    }

    private void Populate(DesktopLayout layout)
    {
        SelectedMonitor = null; // the view models are about to be rebuilt
        Monitors.Clear();
        foreach (var m in layout.Monitors)
            Monitors.Add(new MonitorViewModel(m, this));
        RecomputeTransform();
    }
}
