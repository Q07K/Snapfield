using Snapfield.App.Mvvm;
using Snapfield.Core.Geometry;
using Snapfield.Core.Model;

namespace Snapfield.App.ViewModels;

/// <summary>
/// One draggable monitor on the calibration canvas. Physical millimetre state is
/// the source of truth; canvas pixel coordinates are derived through the owner's
/// mm→canvas transform, so they stay correct across zoom and window resize.
/// </summary>
public sealed class MonitorViewModel : ObservableObject
{
    private readonly CalibrationViewModel _owner;

    public MonitorViewModel(MonitorInfo m, CalibrationViewModel owner)
    {
        _owner = owner;
        MachineId = m.MachineId;
        DeviceId = m.DeviceId;
        DisplayName = string.IsNullOrWhiteSpace(m.DisplayName) ? m.DeviceId : m.DisplayName;
        PixelWidth = m.PixelBounds.Width;
        PixelHeight = m.PixelBounds.Height;
        PixelLeft = m.PixelBounds.Left;
        PixelTop = m.PixelBounds.Top;
        DpiScale = m.DpiScale;
        _kind = m.EffectiveKind;
        _xMm = m.PhysicalBounds.XMm;
        _yMm = m.PhysicalBounds.YMm;
        WidthMm = m.PhysicalBounds.WidthMm;
        HeightMm = m.PhysicalBounds.HeightMm;
    }

    public string MachineId { get; }
    public string DeviceId { get; }
    public string DisplayName { get; }

    /// <summary>What the canvas prints for the owning machine — its nickname when
    /// one is set, else the machine id.</summary>
    public string MachineLabel => _owner.ResolveMachineName(MachineId);
    public void RaiseMachineLabelChanged() => OnPropertyChanged(nameof(MachineLabel));

    /// <summary>True when this monitor belongs to another machine on the plane.</summary>
    public bool IsRemote => MachineId != Environment.MachineName;

    /// <summary>Device kind — drives the silhouette (monitor stand / laptop deck /
    /// phone-tablet slab). Auto-detected (Windows: INTERNAL panel = laptop; Android
    /// reports phone/tablet), but the user can cycle it when detection is wrong.</summary>
    private DeviceKind _kind;
    public DeviceKind Kind
    {
        get => _kind;
        set
        {
            if (!SetField(ref _kind, value)) return;
            OnPropertyChanged(nameof(IsMonitor));
            OnPropertyChanged(nameof(IsLaptop));
            OnPropertyChanged(nameof(IsHandheld));
            OnPropertyChanged(nameof(KindLabel));
        }
    }

    public bool IsMonitor => _kind == DeviceKind.Monitor;
    public bool IsLaptop => _kind == DeviceKind.Laptop;
    /// <summary>Phone or tablet: a bare slab — no stand, no keyboard deck.</summary>
    public bool IsHandheld => _kind is DeviceKind.Phone or DeviceKind.Tablet;

    public string KindLabel => _kind switch
    {
        DeviceKind.Laptop => "노트북",
        DeviceKind.Phone => "스마트폰",
        DeviceKind.Tablet => "태블릿",
        _ => "모니터",
    };

    /// <summary>Manual correction: cycle 모니터 → 노트북 → 스마트폰 → 태블릿.</summary>
    public void CycleKind()
    {
        Kind = _kind switch
        {
            DeviceKind.Monitor => DeviceKind.Laptop,
            DeviceKind.Laptop => DeviceKind.Phone,
            DeviceKind.Phone => DeviceKind.Tablet,
            _ => DeviceKind.Monitor,
        };
    }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public int PixelLeft { get; }
    public int PixelTop { get; }
    public double DpiScale { get; }
    public double WidthMm { get; private set; }
    public double HeightMm { get; private set; }

    private double _xMm;
    public double XMm { get => _xMm; private set { if (SetField(ref _xMm, value)) OnPropertyChanged(nameof(CanvasLeft)); } }

    private double _yMm;
    public double YMm { get => _yMm; private set { if (SetField(ref _yMm, value)) { OnPropertyChanged(nameof(CanvasTop)); OnPropertyChanged(nameof(ZOrder)); } } }

    /// <summary>Painter order: lower-on-the-desk devices draw on top, so a monitor's
    /// stand never covers a laptop sitting below it.</summary>
    public int ZOrder => (int)Math.Round(_yMm);

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }

    // ── Derived canvas geometry (px) ─────────────────────────────────────────
    public double CanvasLeft => _owner.MmToCanvasX(XMm);
    public double CanvasTop => _owner.MmToCanvasY(YMm);
    public double CanvasWidth => WidthMm * _owner.Scale;
    public double CanvasHeight => HeightMm * _owner.Scale;

    // ── Size-adaptive card content ───────────────────────────────────────────
    // A phone card has no room for five lines of text — small cards show just
    // the name (scaled up, readable), and detail returns as you zoom in. The
    // full spec always lives in the tooltip and the inspector.
    public bool ShowResolution => CanvasWidth >= 64 && CanvasHeight >= 56;
    public bool ShowMeta => CanvasWidth >= 110 && CanvasHeight >= 84;

    /// <summary>Hover tooltip: the whole spec, independent of card size.</summary>
    public string Summary =>
        $"{MachineLabel} · {KindLabel}\n{ResolutionText} · {PhysicalText} · {DiagonalText}";

    // ── Labels ───────────────────────────────────────────────────────────────
    public string ResolutionText => $"{PixelWidth} × {PixelHeight}";
    public string PhysicalText => $"{WidthMm:0} × {HeightMm:0} mm";
    public string DiagonalText
    {
        get
        {
            var diagMm = Math.Sqrt(WidthMm * WidthMm + HeightMm * HeightMm);
            return $"{diagMm / 25.4:0.0}\"";
        }
    }

    /// <summary>Overrides the physical size (EDID lies are common — TVs, capture
    /// dongles, some monitors report nonsense image sizes).</summary>
    public void SetPhysicalSize(double widthMm, double heightMm)
    {
        WidthMm = widthMm;
        HeightMm = heightMm;
        OnPropertyChanged(nameof(PhysicalText));
        OnPropertyChanged(nameof(DiagonalText));
        RefreshCanvas();
    }

    /// <summary>Applies a saved placement exactly (preset apply) — no edge snapping.</summary>
    public void ApplyPlacement(double xMm, double yMm, double widthMm, double heightMm)
    {
        WidthMm = widthMm;
        HeightMm = heightMm;
        XMm = xMm;
        YMm = yMm;
        OnPropertyChanged(nameof(PhysicalText));
        OnPropertyChanged(nameof(DiagonalText));
        RefreshCanvas();
    }

    /// <summary>Re-raise canvas coordinates after the owner's transform changes.</summary>
    public void RefreshCanvas()
    {
        OnPropertyChanged(nameof(CanvasLeft));
        OnPropertyChanged(nameof(CanvasTop));
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(ShowResolution));
        OnPropertyChanged(nameof(ShowMeta));
    }

    /// <summary>Move to a new physical position (mm), applying edge snapping to neighbours.</summary>
    public void DragTo(double newXMm, double newYMm)
    {
        var (sx, sy) = _owner.SnapEdges(this, newXMm, newYMm);
        XMm = sx;
        YMm = sy;
        _owner.RefreshSeams(); // crossing bands follow the monitor live (throttled)
    }

    public MonitorInfo ToMonitorInfo() => new()
    {
        MachineId = MachineId,
        DeviceId = DeviceId,
        DisplayName = DisplayName,
        PixelBounds = new PixelRect(PixelLeft, PixelTop, PixelWidth, PixelHeight),
        PhysicalBounds = new PhysicalRect(XMm, YMm, WidthMm, HeightMm),
        DpiScale = DpiScale,
        IsInternal = IsLaptop, // kept for older peers reading the plane
        Kind = _kind,
    };
}
