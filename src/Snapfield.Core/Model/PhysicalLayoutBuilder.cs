using Snapfield.Core.Geometry;

namespace Snapfield.Core.Model;

/// <summary>
/// Builds physical-plane layouts. Two helpers:
///   * <see cref="WindowsAligned"/> positions one machine's monitors on the plane
///     from their Windows pixel arrangement (real EDID sizes, OS ordering/offsets),
///     so edge-detection (pixel space) and routing (physical space) always agree.
///   * <see cref="AppendToRight"/> glues another machine's monitors immediately to
///     the right of a base layout, vertically centred — used to place a remote
///     peer (or the phantom test screen) next to the local monitors.
/// </summary>
public static class PhysicalLayoutBuilder
{
    private const double FallbackMmPerPx = 0.2645; // ~96 DPI

    public static List<MonitorInfo> WindowsAligned(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return new List<MonitorInfo>();

        // Primary ≈ the monitor nearest the pixel origin (0,0).
        var primary = monitors.OrderBy(m => Math.Abs(m.PixelBounds.Left) + Math.Abs(m.PixelBounds.Top)).First();
        var mmPerPx = primary.PixelsPerMmX > 0 ? 1.0 / primary.PixelsPerMmX : FallbackMmPerPx;

        return monitors.Select(m => m with
        {
            PhysicalBounds = new PhysicalRect(
                m.PixelBounds.Left * mmPerPx,
                m.PixelBounds.Top * mmPerPx,
                m.PhysicalBounds.WidthMm > 0 ? m.PhysicalBounds.WidthMm : m.PixelBounds.Width * mmPerPx,
                m.PhysicalBounds.HeightMm > 0 ? m.PhysicalBounds.HeightMm : m.PixelBounds.Height * mmPerPx),
        }).ToList();
    }

    /// <summary>
    /// Returns <paramref name="baseLocal"/> plus <paramref name="remote"/> shifted so the
    /// remote block sits just right of the base, centred on the base's right-most monitor.
    /// Keeps every monitor's PixelBounds untouched (routing back to pixels stays correct).
    /// </summary>
    public static List<MonitorInfo> AppendToRight(IReadOnlyList<MonitorInfo> baseLocal, IReadOnlyList<MonitorInfo> remote)
    {
        var combined = new List<MonitorInfo>(baseLocal);
        if (remote.Count == 0 || baseLocal.Count == 0)
        {
            combined.AddRange(remote);
            return combined;
        }

        var anchor = baseLocal.OrderByDescending(m => m.PhysicalBounds.Right).First();
        var baseRight = anchor.PhysicalBounds.Right;
        var anchorCenterY = anchor.PhysicalBounds.Center.YMm;

        var remoteAligned = WindowsAligned(remote);
        var rMinX = remoteAligned.Min(m => m.PhysicalBounds.XMm);
        var rMinY = remoteAligned.Min(m => m.PhysicalBounds.YMm);
        var rMaxY = remoteAligned.Max(m => m.PhysicalBounds.Bottom);
        var rCenterY = (rMinY + rMaxY) / 2;

        var dx = baseRight - rMinX;
        var dy = anchorCenterY - rCenterY;

        foreach (var r in remoteAligned)
        {
            combined.Add(r with
            {
                PhysicalBounds = r.PhysicalBounds with
                {
                    XMm = r.PhysicalBounds.XMm + dx,
                    YMm = r.PhysicalBounds.YMm + dy,
                },
            });
        }
        return combined;
    }
}
