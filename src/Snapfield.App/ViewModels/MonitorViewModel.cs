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
        IsLaptop = m.IsInternal;
        _xMm = m.PhysicalBounds.XMm;
        _yMm = m.PhysicalBounds.YMm;
        WidthMm = m.PhysicalBounds.WidthMm;
        HeightMm = m.PhysicalBounds.HeightMm;
    }

    public string MachineId { get; }
    public string DeviceId { get; }
    public string DisplayName { get; }

    /// <summary>True when this monitor belongs to another machine on the plane.</summary>
    public bool IsRemote => MachineId != Environment.MachineName;

    /// <summary>Built-in laptop panel vs standalone monitor — drives the silhouette.</summary>
    public bool IsLaptop { get; }
    public bool IsMonitor => !IsLaptop;
    public string KindLabel => IsLaptop ? "노트북" : "모니터";
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
    public double YMm { get => _yMm; private set { if (SetField(ref _yMm, value)) OnPropertyChanged(nameof(CanvasTop)); } }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }

    // ── Derived canvas geometry (px) ─────────────────────────────────────────
    public double CanvasLeft => _owner.MmToCanvasX(XMm);
    public double CanvasTop => _owner.MmToCanvasY(YMm);
    public double CanvasWidth => WidthMm * _owner.Scale;
    public double CanvasHeight => HeightMm * _owner.Scale;

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

    /// <summary>Re-raise canvas coordinates after the owner's transform changes.</summary>
    public void RefreshCanvas()
    {
        OnPropertyChanged(nameof(CanvasLeft));
        OnPropertyChanged(nameof(CanvasTop));
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
    }

    /// <summary>Move to a new physical position (mm), applying edge snapping to neighbours.</summary>
    public void DragTo(double newXMm, double newYMm)
    {
        var (sx, sy) = _owner.SnapEdges(this, newXMm, newYMm);
        XMm = sx;
        YMm = sy;
    }

    public MonitorInfo ToMonitorInfo() => new()
    {
        MachineId = MachineId,
        DeviceId = DeviceId,
        DisplayName = DisplayName,
        PixelBounds = new PixelRect(PixelLeft, PixelTop, PixelWidth, PixelHeight),
        PhysicalBounds = new PhysicalRect(XMm, YMm, WidthMm, HeightMm),
        DpiScale = DpiScale,
        IsInternal = IsLaptop,
    };
}
