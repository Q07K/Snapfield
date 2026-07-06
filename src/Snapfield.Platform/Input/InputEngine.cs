using System.Runtime.InteropServices;
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
    private readonly LowLevelKeyboardHook _kbHook;
    private DesktopLayout _layout; // for the machine-switch cycle order

    // The router and capture state are touched by three threads: the mouse-hook
    // thread (moves), the keyboard-hook thread (panic release), and network
    // reader threads (layout updates on peer join/leave). One small lock keeps
    // them consistent — every guarded section is pure in-memory work, so hook
    // callbacks stay fast.
    private readonly object _sync = new();

    /// <summary>Local pixel→mm scale and the parking point, swapped atomically as
    /// one immutable snapshot so hook threads never see a half-updated pair.</summary>
    private sealed record LocalMetrics(double MmPerPx, int CenterX, int CenterY);
    private LocalMetrics _metrics = new(0.25, 0, 0); // fallback ~96 DPI; recomputed from layout

    private volatile bool _captured;
    private int _lastStatusTick;

    /// <summary>Cursor speed multiplier while traversing remote screens.</summary>
    public double Sensitivity { get; set; } = 1.0;

    /// <summary>Hide the (parked) local cursor while control is on a remote machine.</summary>
    public bool HideCursorWhileCaptured { get; set; } = true;

    public event Action<EngineStatus>? StatusChanged;

    /// <summary>Non-fatal engine failures the UI should surface (e.g. a global
    /// hook could not be installed).</summary>
    public event Action<string>? Fault;

    // Raised while control is on a remote monitor, for the network layer to forward.
    public event Action<string, int, int>? RemoteCursor;   // (remoteMachineId, x, y)
    public event Action<int, bool>? RemoteButton;          // (button, down)
    public event Action<int, bool>? RemoteWheel;           // (delta, horizontal)
    public event Action<int, int, bool, bool>? RemoteKey;  // (vk, scan, down, extended)
    public event Action<string>? ControlEnteredRemote;     // (remoteMachineId)
    public event Action? ControlReturnedLocal;

    public InputEngine(string localMachineId, DesktopLayout layout)
    {
        _localMachineId = localMachineId;
        _layout = layout;
        _router = new CursorRouter(localMachineId, layout);
        _hook = new LowLevelMouseHook(OnMouseEvent);
        _kbHook = new LowLevelKeyboardHook(OnKeyEvent);
        _hook.Failed += m => Fault?.Invoke("마우스 훅 설치 실패 — 원격 조작이 동작하지 않습니다: " + m);
        _kbHook.Failed += m => Fault?.Invoke("키보드 훅 설치 실패 — 원격 키 입력이 동작하지 않습니다: " + m);
        RecomputeFromLayout(layout);
    }

    public bool IsRunning { get; private set; }

    public void UpdateLayout(DesktopLayout layout)
    {
        lock (_sync)
        {
            _layout = layout;
            _router.UpdateLayout(layout);
            RecomputeFromLayout(layout);
        }
    }

    private void RecomputeFromLayout(DesktopLayout layout)
    {
        var local = layout.Monitors.FirstOrDefault(m => m.MachineId == _localMachineId);
        if (local is null) return;
        var mmPerPx = local.PixelsPerMmX > 0 ? 1.0 / local.PixelsPerMmX : _metrics.MmPerPx;
        _metrics = new LocalMetrics(
            mmPerPx,
            local.PixelBounds.Left + local.PixelBounds.Width / 2,
            local.PixelBounds.Top + local.PixelBounds.Height / 2);
    }

    public void Start()
    {
        if (IsRunning) return;
        var (x, y) = CursorInjector.GetPosition();
        lock (_sync)
        {
            _router.SeatLocal(x, y);
            _captured = false;
        }
        _hook.Start();
        _kbHook.Start();
        IsRunning = true;
        RaiseStatus(force: true);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _hook.Stop();
        _kbHook.Stop();
        _captured = false;
        CursorHider.Show(); // never leave the system cursor hidden
        IsRunning = false;
        RaiseStatus(force: true);
    }

    // Runs on the keyboard-hook thread — keep it fast. While captured, keys go
    // to the remote machine and are swallowed locally; when local, pass through.
    private int _ctrlTaps;
    private int _ctrlTapTick;

    private bool OnKeyEvent(KeyHookEvent e)
    {
        if (e.InjectedByUs) return false;

        // Machine switch: Ctrl+Alt+←/→ warps the cursor to the previous/next
        // machine on the plane — works from local AND while captured.
        if (!e.Up && e.Vk is 0x25 or 0x27 && CtrlAltHeld())
        {
            JumpBy(e.Vk == 0x27 ? +1 : -1);
            return true; // swallow — don't let the arrow reach apps
        }

        if (!_captured) return false;

        // Panic release: tap Ctrl 3× quickly to yank control back to this PC,
        // e.g. if the remote hangs. Any other key resets the count.
        if (IsPanicTap(e)) { ForceReleaseToLocal(); return true; }

        RemoteKey?.Invoke(e.Vk, e.Scan, !e.Up, e.Extended);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);
    private static bool CtrlAltHeld() => GetAsyncKeyState(0x11) < 0 && GetAsyncKeyState(0x12) < 0;

    /// <summary>Cycles the cursor to the previous/next machine, ordered left→right
    /// by each machine's leftmost monitor on the physical plane.</summary>
    private void JumpBy(int dir)
    {
        string? target;
        lock (_sync)
        {
            var machines = _layout.Monitors
                .GroupBy(m => m.MachineId)
                .OrderBy(g => g.Min(m => m.PhysicalBounds.XMm))
                .Select(g => g.Key)
                .ToList();
            if (machines.Count < 2) return;
            var current = _router.Active?.MachineId ?? _localMachineId;
            var idx = machines.IndexOf(current);
            target = machines[((idx < 0 ? 0 : idx) + dir + machines.Count) % machines.Count];
        }
        JumpToMachine(target);
    }

    /// <summary>Warps the cursor to the centre of a machine's largest monitor —
    /// the fast path across a wide plane, no seam-pushing required.</summary>
    public void JumpToMachine(string machineId)
    {
        lock (_sync)
        {
            var res = _router.JumpToMachine(machineId);
            if (res.Owner is null) return;
            var (px, py) = res.PixelInt;

            if (res.Owner.MachineId == _localMachineId)
            {
                // Land on a local monitor: release capture (if any) and warp there.
                if (_captured)
                {
                    _captured = false;
                    CursorHider.Show();
                    ControlReturnedLocal?.Invoke();
                }
                CursorInjector.WarpTo(px, py);
            }
            else
            {
                // Land on a remote monitor: park + capture, like a seam crossing.
                if (!_captured)
                {
                    _captured = true;
                    if (HideCursorWhileCaptured) CursorHider.Hide();
                    var m = _metrics;
                    CursorInjector.WarpTo(m.CenterX, m.CenterY);
                }
                ControlEnteredRemote?.Invoke(res.Owner.MachineId); // retargets routing + Enter msg
                RemoteCursor?.Invoke(res.Owner.MachineId, px, py);
            }
            RaiseStatus(force: true);
        }
    }

    private bool IsPanicTap(KeyHookEvent e)
    {
        var isCtrl = e.Vk is 0x11 or 0xA2 or 0xA3; // VK_CONTROL / L / R
        if (!isCtrl) { _ctrlTaps = 0; return false; }
        if (!e.Up) return false;                    // count on release, not auto-repeat
        var now = Environment.TickCount;
        if (now - _ctrlTapTick > 700) _ctrlTaps = 0;
        _ctrlTapTick = now;
        if (++_ctrlTaps < 3) return false;
        _ctrlTaps = 0;
        return true;
    }

    /// <summary>Immediately returns control to this PC (panic hotkey / peer-drop
    /// safety — without it a dead peer strands the cursor invisible).</summary>
    public void ForceReleaseToLocal()
    {
        if (!_captured) return;
        int cx, cy;
        lock (_sync)
        {
            if (!_captured) return; // lost the race to a normal ToLocal transition
            _captured = false;
            var m = _metrics;
            cx = m.CenterX; cy = m.CenterY;
            _router.SeatLocal(cx, cy);
        }
        CursorHider.Show();
        CursorInjector.WarpTo(cx, cy);
        ControlReturnedLocal?.Invoke();
        RaiseStatus(force: true);
    }

    // Runs on the hook thread — keep it fast.
    private bool OnMouseEvent(MouseHookEvent e)
    {
        if (e.InjectedByUs) return false; // ignore our own warps

        if (e.Message == WM_MOUSEMOVE)
            return OnMove(e.X, e.Y);

        // Buttons / wheel: forward to the remote and swallow locally while captured
        // (they would otherwise act on the parked local cursor). Pass through when local.
        if (_captured)
        {
            ForwardButtonOrWheel(e);
            return true;
        }
        return false;
    }

    private bool OnMove(int x, int y)
    {
        lock (_sync)
        {
            if (!_captured)
            {
                var res = _router.OnLocalAbsolute(x, y);
                if (res.Transition == RouteTransition.ToRemote)
                {
                    EnterCapture(res);
                    return true; // stop this move; cursor is now parked
                }
                return false; // normal local movement
            }

            // Captured: convert the delta from the parked centre into physical mm.
            var m = _metrics;
            var dxMm = (x - m.CenterX) * m.MmPerPx * Sensitivity;
            var dyMm = (y - m.CenterY) * m.MmPerPx * Sensitivity;
            var result = _router.OnDelta(dxMm, dyMm);

            if (result.Transition == RouteTransition.ToLocal)
            {
                _captured = false;
                CursorHider.Show();
                var (px, py) = result.PixelInt;
                CursorInjector.WarpTo(px, py);
                ControlReturnedLocal?.Invoke();
                RaiseStatus(force: true);
                return true;
            }

            // Still remote: stream the position to the remote, then reset the parking
            // point so we always have movement headroom locally.
            if (result.Owner is not null)
            {
                var (px, py) = result.PixelInt;
                RemoteCursor?.Invoke(result.Owner.MachineId, px, py);
            }
            CursorInjector.WarpTo(m.CenterX, m.CenterY);
            RaiseStatus(force: false);
            return true;
        }
    }

    // Called with _sync held (from OnMove).
    private void EnterCapture(Snapfield.Core.Input.RouteResult res)
    {
        _captured = true;
        if (HideCursorWhileCaptured) CursorHider.Hide();
        var m = _metrics;
        CursorInjector.WarpTo(m.CenterX, m.CenterY);
        if (res.Owner is not null)
        {
            ControlEnteredRemote?.Invoke(res.Owner.MachineId);
            var (px, py) = res.PixelInt;
            RemoteCursor?.Invoke(res.Owner.MachineId, px, py); // seed remote cursor at entry point
        }
        RaiseStatus(force: true);
    }

    private void ForwardButtonOrWheel(MouseHookEvent e)
    {
        switch (e.Message)
        {
            case WM_LBUTTONDOWN: RemoteButton?.Invoke(0, true); break;
            case WM_LBUTTONUP: RemoteButton?.Invoke(0, false); break;
            case WM_RBUTTONDOWN: RemoteButton?.Invoke(1, true); break;
            case WM_RBUTTONUP: RemoteButton?.Invoke(1, false); break;
            case WM_MBUTTONDOWN: RemoteButton?.Invoke(2, true); break;
            case WM_MBUTTONUP: RemoteButton?.Invoke(2, false); break;
            case WM_MOUSEWHEEL: RemoteWheel?.Invoke(unchecked((short)(e.MouseData >> 16)), false); break;
            case WM_MOUSEHWHEEL: RemoteWheel?.Invoke(unchecked((short)(e.MouseData >> 16)), true); break;
        }
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
