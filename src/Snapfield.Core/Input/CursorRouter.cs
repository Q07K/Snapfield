using Snapfield.Core.Geometry;
using Snapfield.Core.Model;
using Snapfield.Core.Transforms;

namespace Snapfield.Core.Input;

/// <summary>
/// The machine-independent brain of the input engine. It tracks a single "virtual
/// cursor" on the global physical plane and decides, on every movement, which
/// monitor now owns it and whether control just crossed the local/remote seam.
///
/// Two kinds of input drive it:
///   * <see cref="OnLocalAbsolute"/> — while the cursor is on THIS machine, the OS
///     reports absolute pixel positions. The router watches for the cursor pressing
///     against an edge whose physical neighbour is a remote monitor, and hands off.
///   * <see cref="OnDelta"/> — once control is remote, the local cursor is pinned and
///     we feed it raw movement deltas (in mm). When the virtual cursor re-enters a
///     local monitor, the router asks the engine to warp the real cursor back.
///
/// Pure and deterministic: no Win32, fully unit-testable.
/// </summary>
public sealed class CursorRouter
{
    private readonly string _localMachineId;
    private CoordinateMapper _mapper;
    private DesktopLayout _layout;

    /// <summary>How far (mm) to probe across an edge when testing for a remote neighbour.</summary>
    public double ProbeMm { get; set; } = 1.0;

    /// <summary>
    /// Second, longer probe (mm) so a small calibration gap between a local edge
    /// and the remote monitor doesn't make the seam impossible to cross.
    /// </summary>
    public double GapProbeMm { get; set; } = 30.0;

    public CursorRouter(string localMachineId, DesktopLayout layout)
    {
        _localMachineId = localMachineId;
        _layout = layout;
        _mapper = new CoordinateMapper(layout);
    }

    public PhysicalPoint Virtual { get; private set; }
    public MonitorInfo? Active { get; private set; }
    public bool IsLocalActive => Active is not null && Active.MachineId == _localMachineId;

    public void UpdateLayout(DesktopLayout layout)
    {
        _layout = layout;
        _mapper = new CoordinateMapper(layout);
    }

    /// <summary>Initialise the virtual cursor from a real local pixel position.</summary>
    public void SeatLocal(double pixelX, double pixelY)
    {
        var p = _mapper.PixelToPhysical(_localMachineId, pixelX, pixelY);
        if (p is null)
        {
            Active = _mapper.NearestMonitor(default);
            Virtual = Active?.PhysicalBounds.Center ?? default;
            return;
        }
        Virtual = p.Value;
        Active = _mapper.HitTest(p.Value) ?? _mapper.NearestMonitor(p.Value);
    }

    /// <summary>
    /// Feed an absolute local cursor position (pixels in this machine's virtual
    /// desktop). Returns a handoff decision when the cursor presses against an edge
    /// backed by a remote monitor.
    /// </summary>
    public RouteResult OnLocalAbsolute(double pixelX, double pixelY)
    {
        var phys = _mapper.PixelToPhysical(_localMachineId, pixelX, pixelY);
        if (phys is null) return new RouteResult(RouteTransition.None, Active, 0, 0);

        var p = phys.Value;
        Virtual = p;
        var owner = _mapper.HitTest(p);
        if (owner is not null) Active = owner;

        // Only a LOCAL monitor can be under an absolute local position. Look for a
        // remote monitor just across whichever edge(s) the cursor is pinned against.
        var handoff = ProbeRemoteAcrossEdges(owner ?? Active, pixelX, pixelY, p);
        if (handoff is not null)
        {
            Virtual = handoff.Value.Seed;
            Active = handoff.Value.Monitor;
            var (rx, ry) = _mapper.PhysicalToPixel(handoff.Value.Monitor, handoff.Value.Seed);
            return new RouteResult(RouteTransition.ToRemote, handoff.Value.Monitor, rx, ry);
        }
        return new RouteResult(RouteTransition.None, Active, pixelX, pixelY);
    }

    /// <summary>
    /// Feed a movement delta (mm) while control is remote/captured. Moves the virtual
    /// cursor and reports whether it has returned to a local monitor.
    /// </summary>
    public RouteResult OnDelta(double dxMm, double dyMm)
    {
        var p = new PhysicalPoint(Virtual.XMm + dxMm, Virtual.YMm + dyMm);
        var owner = _mapper.HitTest(p);
        if (owner is null)
        {
            // In a gap or off-plane: keep the cursor pinned to the nearest monitor.
            owner = _mapper.NearestMonitor(p);
            if (owner is not null) p = owner.PhysicalBounds.Clamp(p);
        }

        var wasLocal = IsLocalActive;
        Virtual = p;
        Active = owner;
        var nowLocal = IsLocalActive;

        var (px, py) = owner is null ? (0.0, 0.0) : _mapper.PhysicalToPixel(owner, p);

        var transition = (wasLocal, nowLocal) switch
        {
            (false, true) => RouteTransition.ToLocal,
            (true, false) => RouteTransition.ToRemote,
            _ => RouteTransition.None,
        };
        return new RouteResult(transition, owner, px, py);
    }

    // ── Edge probing ─────────────────────────────────────────────────────────

    private readonly record struct Handoff(MonitorInfo Monitor, PhysicalPoint Seed);

    /// <summary>
    /// If the cursor is pinned against a local monitor edge and a remote monitor sits
    /// physically just beyond it, returns that monitor plus a seed point just inside it.
    /// </summary>
    private Handoff? ProbeRemoteAcrossEdges(MonitorInfo? local, double pixelX, double pixelY, PhysicalPoint p)
    {
        if (local is null) return null;
        var pb = local.PixelBounds;

        // Right edge.
        if (pixelX >= pb.Right - 1)
        {
            var h = ProbeDirection(local, p, dx: +1, dy: 0);
            if (h is not null) return h;
        }
        // Left edge.
        if (pixelX <= pb.Left)
        {
            var h = ProbeDirection(local, p, dx: -1, dy: 0);
            if (h is not null) return h;
        }
        // Bottom edge.
        if (pixelY >= pb.Bottom - 1)
        {
            var h = ProbeDirection(local, p, dx: 0, dy: +1);
            if (h is not null) return h;
        }
        // Top edge.
        if (pixelY <= pb.Top)
        {
            var h = ProbeDirection(local, p, dx: 0, dy: -1);
            if (h is not null) return h;
        }
        return null;
    }

    /// <summary>
    /// Probes just beyond the given edge, then again across a small gap. A local
    /// monitor at the near probe blocks the seam (Windows owns that transition);
    /// an empty near probe lets the far probe bridge a calibration gap.
    /// </summary>
    private Handoff? ProbeDirection(MonitorInfo local, PhysicalPoint p, int dx, int dy)
    {
        foreach (var distance in new[] { ProbeMm, GapProbeMm })
        {
            var probe = new PhysicalPoint(
                dx == 0 ? p.XMm : (dx > 0 ? local.PhysicalBounds.Right : local.PhysicalBounds.XMm) + dx * distance,
                dy == 0 ? p.YMm : (dy > 0 ? local.PhysicalBounds.Bottom : local.PhysicalBounds.YMm) + dy * distance);

            var m = _mapper.HitTest(probe);
            if (m is null) continue;                       // gap — try the longer probe
            if (m.MachineId == _localMachineId) return null; // a local monitor owns this seam
            return new Handoff(m, m.PhysicalBounds.Clamp(probe));
        }
        return null;
    }
}
