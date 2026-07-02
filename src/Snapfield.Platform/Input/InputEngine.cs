using Snapfield.Core.Input;
using Snapfield.Core.Model;
using static Snapfield.Platform.Input.InputInterop;

namespace Snapfield.Platform.Input;

/// <summary>Snapshot of engine state for the UI.</summary>
public readonly record struct EngineStatus(
    bool Running,
    bool Captured,
    string ActiveMonitor,
    bool ActiveIsRemote,
    double VirtualXMm,
    double VirtualYMm);

/// <summary>
/// Ties the low-level hook, the coordinate router, and cursor injection into one
/// running engine.
///
/// While the cursor is on a LOCAL monitor it moves normally; the engine only
/// watches for it pressing against an edge backed by a remote monitor. On such a
/// crossing it "captures": parks the local cursor at screen centre and feeds raw
/// movement deltas to the router, which walks a virtual cursor across the remote
/// screen(s). When the virtual cursor returns to a local monitor the engine warps
/// the real cursor to the matching physical position and releases capture.
///
/// (Remote delivery is not wired yet — this is the local half, testable with a
/// phantom monitor in the layout.)
/// </summary>
public sealed class InputEngine : IDisposable
{
    private readonly string _localMachineId;
    private readonly CursorRouter _router;
    private readonly LowLevelMouseHook _hook;

    private double _mmPerPx = 0.25;      // fallback ~96 DPI; recomputed from layout
    private (int X, int Y) _center;      // local parking point during capture
    private bool _captured;
    private int _lastStatusTick;

    /// <summary>Cursor speed multiplier while traversing remote screens.</summary>
    public double Sensitivity { get; set; } = 1.0;

    public event Action<EngineStatus>? StatusChanged;

    public InputEngine(string localMachineId, DesktopLayout layout)
    {
        _localMachineId = localMachineId;
        _router = new CursorRouter(localMachineId, layout);
        _hook = new LowLevelMouseHook(OnMouseEvent);
        RecomputeFromLayout(layout);
    }

    public bool IsRunning { get; private set; }

    public void UpdateLayout(DesktopLayout layout)
    {
        _router.UpdateLayout(layout);
        RecomputeFromLayout(layout);
    }

    private void RecomputeFromLayout(DesktopLayout layout)
    {
        var local = layout.Monitors.FirstOrDefault(m => m.MachineId == _localMachineId);
        if (local is not null)
        {
            if (local.PixelsPerMmX > 0) _mmPerPx = 1.0 / local.PixelsPerMmX;
            _center = (local.PixelBounds.Left + local.PixelBounds.Width / 2,
                       local.PixelBounds.Top + local.PixelBounds.Height / 2);
        }
    }

    public void Start()
    {
        if (IsRunning) return;
        var (x, y) = CursorInjector.GetPosition();
        _router.SeatLocal(x, y);
        _captured = false;
        _hook.Start();
        IsRunning = true;
        RaiseStatus(force: true);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _hook.Stop();
        _captured = false;
        IsRunning = false;
        RaiseStatus(force: true);
    }

    // Runs on the hook thread — keep it fast.
    private bool OnMouseEvent(MouseHookEvent e)
    {
        if (e.InjectedByUs) return false; // ignore our own warps

        if (e.Message == WM_MOUSEMOVE)
            return OnMove(e.X, e.Y);

        // Buttons / wheel: swallow everything while captured (would otherwise act
        // on the parked local cursor). Pass through when local.
        return _captured;
    }

    private bool OnMove(int x, int y)
    {
        if (!_captured)
        {
            var res = _router.OnLocalAbsolute(x, y);
            if (res.Transition == RouteTransition.ToRemote)
            {
                EnterCapture();
                return true; // stop this move; cursor is now parked
            }
            return false; // normal local movement
        }

        // Captured: convert the delta from the parked centre into physical mm.
        var dxMm = (x - _center.X) * _mmPerPx * Sensitivity;
        var dyMm = (y - _center.Y) * _mmPerPx * Sensitivity;
        var result = _router.OnDelta(dxMm, dyMm);

        if (result.Transition == RouteTransition.ToLocal)
        {
            _captured = false;
            var (px, py) = result.PixelInt;
            CursorInjector.WarpTo(px, py);
            RaiseStatus(force: true);
            return true;
        }

        // Still remote: reset the parking point so we always have headroom.
        CursorInjector.WarpTo(_center.X, _center.Y);
        RaiseStatus(force: false);
        return true;
    }

    private void EnterCapture()
    {
        _captured = true;
        CursorInjector.WarpTo(_center.X, _center.Y);
        RaiseStatus(force: true);
    }

    private void RaiseStatus(bool force)
    {
        var now = Environment.TickCount;
        if (!force && now - _lastStatusTick < 30) return; // throttle to ~33 Hz
        _lastStatusTick = now;

        var active = _router.Active;
        StatusChanged?.Invoke(new EngineStatus(
            Running: IsRunning,
            Captured: _captured,
            ActiveMonitor: active is null ? "—" : (string.IsNullOrEmpty(active.DisplayName) ? active.DeviceId : active.DisplayName),
            ActiveIsRemote: active is not null && active.MachineId != _localMachineId,
            VirtualXMm: _router.Virtual.XMm,
            VirtualYMm: _router.Virtual.YMm));
    }

    public void Dispose() => Stop();
}
