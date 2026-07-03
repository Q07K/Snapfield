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
    /// How far beyond an edge (mm) a remote monitor may sit and still be reachable
    /// by hitting that edge. Absorbs calibration gaps and lets a fast, slightly
    /// off-band flick still cross to the remote on that side.
    /// </summary>
    public double SeamGapMm { get; set; } = 80.0;

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

    private enum Edge { Right, Left, Down, Up }
    private readonly record struct Handoff(MonitorInfo Monitor, PhysicalPoint Seed);

    /// <summary>
    /// When the cursor is pinned against a local monitor edge, decide whether it
    /// crosses to a remote monitor. First a point-probe just across the seam at the
    /// cursor's position handles the aligned case and lets a LOCAL neighbour block
    /// the seam; if that misses, an edge scan finds the nearest remote monitor
    /// beyond that edge and hands off to it — so a fast flick that lands slightly
    /// off the remote's band still crosses (entry is clamped into the remote).
    /// </summary>
    private Handoff? ProbeRemoteAcrossEdges(MonitorInfo? local, double pixelX, double pixelY, PhysicalPoint p)
    {
        if (local is null) return null;
        var pb = local.PixelBounds;
        if (pixelX >= pb.Right - 1) { var h = Cross(local, Edge.Right, p.YMm); if (h is not null) return h; }
        if (pixelX <= pb.Left)      { var h = Cross(local, Edge.Left,  p.YMm); if (h is not null) return h; }
        if (pixelY >= pb.Bottom - 1){ var h = Cross(local, Edge.Down,  p.XMm); if (h is not null) return h; }
        if (pixelY <= pb.Top)       { var h = Cross(local, Edge.Up,    p.XMm); if (h is not null) return h; }
        return null;
    }

    private Handoff? Cross(MonitorInfo local, Edge edge, double along)
    {
        var lb = local.PhysicalBounds;
        var near = edge switch
        {
            Edge.Right => new PhysicalPoint(lb.Right + ProbeMm, along),
            Edge.Left  => new PhysicalPoint(lb.XMm - ProbeMm, along),
            Edge.Down  => new PhysicalPoint(along, lb.Bottom + ProbeMm),
            _          => new PhysicalPoint(along, lb.YMm - ProbeMm),
        };

        // Aligned case: whatever sits right at the seam. A local monitor there owns
        // the transition (Windows moves the cursor onto it) — don't hijack it.
        var hit = _mapper.HitTest(near);
        if (hit is not null)
            return hit.MachineId == _localMachineId ? null : new Handoff(hit, hit.PhysicalBounds.Clamp(near));

        // Off-band / fast flick: hand off to the nearest remote beyond this edge.
        var best = NearestRemoteBeyond(local, edge, along);
        if (best is null) return null;

        var rb = best.PhysicalBounds;
        var seed = edge switch
        {
            Edge.Right => new PhysicalPoint(rb.XMm + ProbeMm, along),
            Edge.Left  => new PhysicalPoint(rb.Right - ProbeMm, along),
            Edge.Down  => new PhysicalPoint(along, rb.YMm + ProbeMm),
            _          => new PhysicalPoint(along, rb.Bottom - ProbeMm),
        };
        return new Handoff(best, rb.Clamp(seed));
    }

    /// <summary>
    /// The remote monitor lying just beyond the given edge (within <see cref="SeamGapMm"/>),
    /// closest to the cursor's position along the edge. Lets the cursor cross by
    /// hitting the edge anywhere near the remote, not only where it's perfectly aligned.
    /// </summary>
    private MonitorInfo? NearestRemoteBeyond(MonitorInfo local, Edge edge, double along)
    {
        var lb = local.PhysicalBounds;
        MonitorInfo? best = null;
        var bestPerp = double.MaxValue;

        foreach (var m in _layout.Monitors)
        {
            if (m.MachineId == _localMachineId || ReferenceEquals(m, local)) continue;
            var rb = m.PhysicalBounds;

            bool beyond = edge switch
            {
                Edge.Right => rb.XMm >= lb.Right - 1 && rb.XMm <= lb.Right + SeamGapMm,
                Edge.Left  => rb.Right <= lb.XMm + 1 && rb.Right >= lb.XMm - SeamGapMm,
                Edge.Down  => rb.YMm >= lb.Bottom - 1 && rb.YMm <= lb.Bottom + SeamGapMm,
                _          => rb.Bottom <= lb.YMm + 1 && rb.Bottom >= lb.YMm - SeamGapMm,
            };
            if (!beyond) continue;

            // Distance from the cursor's along-edge position to the monitor's span.
            var perp = edge is Edge.Right or Edge.Left
                ? PerpDistance(along, rb.YMm, rb.Bottom)
                : PerpDistance(along, rb.XMm, rb.Right);
            if (perp < bestPerp) { bestPerp = perp; best = m; }
        }
        return best;
    }

    private static double PerpDistance(double v, double lo, double hi) =>
        v < lo ? lo - v : v > hi ? v - hi : 0;
}
