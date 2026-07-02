using Snapfield.Core.Geometry;
using Snapfield.Core.Persistence;

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

        // Uniform similarity transform of the Windows desktop: positions AND sizes
        // share one scale. Mixing pixel-scaled positions with EDID sizes produced
        // overlapping physical rects when densities differ wildly (e.g. a laptop
        // panel next to a TV), which made edge probes hit a local monitor and the
        // remote seam unreachable. Geometric consistency with pixel space is what
        // the engine needs; true EDID placement returns via the calibration UI.
        return monitors.Select(m => m with
        {
            PhysicalBounds = new PhysicalRect(
                m.PixelBounds.Left * mmPerPx,
                m.PixelBounds.Top * mmPerPx,
                m.PixelBounds.Width * mmPerPx,
                m.PixelBounds.Height * mmPerPx),
        }).ToList();
    }

    /// <summary>
    /// Builds the routing layout the way the user calibrated it: every monitor
    /// (local or remote) whose key exists in <paramref name="saved"/> takes its
    /// saved physical placement; local monitors without one fall back to the
    /// Windows-aligned uniform transform, and remote monitors without one are
    /// appended to the right of the plane (the pre-calibration default).
    /// Detected pixel bounds always win over saved ones — the OS is the truth
    /// for pixel space; the calibration file is the truth for physical space.
    /// </summary>
    public static List<MonitorInfo> Calibrated(
        IReadOnlyList<MonitorInfo> localDetected,
        IReadOnlyList<MonitorInfo> remote,
        DesktopLayout? saved)
    {
        var localAligned = WindowsAligned(localDetected);
        var result = new List<MonitorInfo>();
        for (var i = 0; i < localDetected.Count; i++)
        {
            var prior = saved?.Find(localDetected[i].Key);
            result.Add(prior is null
                ? localAligned[i]
                : localDetected[i] with { PhysicalBounds = prior.PhysicalBounds });
        }

        var placedRemote = new List<MonitorInfo>();
        var unplacedRemote = new List<MonitorInfo>();
        foreach (var r in remote)
        {
            var prior = saved?.Find(r.Key);
            if (prior is null) unplacedRemote.Add(r);
            else placedRemote.Add(r with { PhysicalBounds = prior.PhysicalBounds });
        }

        result.AddRange(placedRemote);
        return unplacedRemote.Count > 0 ? AppendToRight(result, unplacedRemote) : result;
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
