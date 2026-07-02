using Snapfield.Core.Geometry;
using Snapfield.Core.Model;

namespace Snapfield.Core.Transforms;

/// <summary>
/// The three-stage coordinate pipeline that turns a cursor movement on one
/// machine into a placement command for whichever monitor the cursor now
/// belongs to — even across machines.
///
///   1. pixel → physical : map a machine's pixel cursor onto the global mm plane
///   2. hit-test         : find which monitor owns that physical point
///   3. physical → pixel : convert back to the target machine's pixel space
///
/// Every conversion is a pure affine scale within a monitor, so the mapper is
/// stateless and trivially unit-testable.
/// </summary>
public sealed class CoordinateMapper
{
    private readonly DesktopLayout _layout;

    public CoordinateMapper(DesktopLayout layout) => _layout = layout;

    // ── Stage 1: pixel → physical ────────────────────────────────────────────

    /// <summary>Maps a pixel point inside a known monitor onto the global plane.</summary>
    public PhysicalPoint PixelToPhysical(MonitorInfo m, double pixelX, double pixelY)
    {
        var fracX = (pixelX - m.PixelBounds.Left) / m.PixelBounds.Width;
        var fracY = (pixelY - m.PixelBounds.Top) / m.PixelBounds.Height;
        return new PhysicalPoint(
            m.PhysicalBounds.XMm + fracX * m.PhysicalBounds.WidthMm,
            m.PhysicalBounds.YMm + fracY * m.PhysicalBounds.HeightMm);
    }

    /// <summary>
    /// Maps a cursor position expressed in a machine's virtual-desktop pixels
    /// onto the global plane, by first locating the monitor it sits on.
    /// Returns null if the point lands on no monitor of that machine.
    /// </summary>
    public PhysicalPoint? PixelToPhysical(string machineId, double pixelX, double pixelY)
    {
        foreach (var m in _layout.MonitorsOf(machineId))
            if (m.PixelBounds.Contains(pixelX, pixelY))
                return PixelToPhysical(m, pixelX, pixelY);
        return null;
    }

    // ── Stage 2: hit-test on the global plane ────────────────────────────────

    /// <summary>The monitor whose physical rectangle contains the point, if any.</summary>
    public MonitorInfo? HitTest(PhysicalPoint p)
    {
        foreach (var m in _layout.Monitors)
            if (m.PhysicalBounds.Contains(p))
                return m;
        return null;
    }

    /// <summary>The monitor physically closest to the point (never null when a layout exists).</summary>
    public MonitorInfo? NearestMonitor(PhysicalPoint p)
    {
        MonitorInfo? best = null;
        var bestDist = double.MaxValue;
        foreach (var m in _layout.Monitors)
        {
            var d = m.PhysicalBounds.DistanceTo(p);
            if (d < bestDist) { bestDist = d; best = m; }
        }
        return best;
    }

    // ── Stage 3: physical → pixel ────────────────────────────────────────────

    /// <summary>Maps a physical point (assumed inside <paramref name="m"/>) to that monitor's pixels.</summary>
    public (double X, double Y) PhysicalToPixel(MonitorInfo m, PhysicalPoint p)
    {
        var fracX = (p.XMm - m.PhysicalBounds.XMm) / m.PhysicalBounds.WidthMm;
        var fracY = (p.YMm - m.PhysicalBounds.YMm) / m.PhysicalBounds.HeightMm;
        return (
            m.PixelBounds.Left + fracX * m.PixelBounds.Width,
            m.PixelBounds.Top + fracY * m.PixelBounds.Height);
    }

    // ── Full resolve ─────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a global physical point to a concrete cursor placement.
    /// Falls back to the nearest monitor (clamping the point onto it) when the
    /// point lands in a gap between monitors, so the cursor is never lost.
    /// Returns null only when the layout is empty.
    /// </summary>
    public CursorLocation? Resolve(PhysicalPoint p)
    {
        var monitor = HitTest(p);
        if (monitor is null)
        {
            monitor = NearestMonitor(p);
            if (monitor is null) return null;
            p = monitor.PhysicalBounds.Clamp(p);
        }
        var (px, py) = PhysicalToPixel(monitor, p);
        return new CursorLocation(monitor, px, py);
    }
}
