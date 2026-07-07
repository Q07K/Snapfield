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
    // them consistent — every guarded section MUST be pure in-memory work.
    // SendInput and SetSystemCursor are NOT in-memory work: SendInput waits on
    // every other low-level hook in the system and pumps sent messages while it
    // waits. Holding _sync across it once wedged the whole engine (hang dump:
    // hook thread inside OnMove → SendInput → re-entrant OnMove → SendInput,
    // panic release and watchdog both parked on _sync behind it). Decide under
    // the lock; warp/hide/show/notify only after releasing it.
    private readonly object _sync = new();

    /// <summary>Local pixel→mm scale and the parking point, swapped atomically as
    /// one immutable snapshot so hook threads never see a half-updated pair.</summary>
    private sealed record LocalMetrics(double MmPerPx, int CenterX, int CenterY);
    private LocalMetrics _metrics = new(0.25, 0, 0); // fallback ~96 DPI; recomputed from layout

    private volatile bool _captured;
    private int _lastStatusTick;

    // Capture watchdog: Windows silently removes a low-level hook whose callback
    // exceeds the hook timeout. If that happens while captured, the mouse comes
    // back locally but the keyboard hook keeps swallowing every key — forever.
    // The watchdog detects the removed hook (see WatchdogTick) and heals.
    private readonly object _lifecycle = new(); // serializes Start/Stop/hook reinstall
    private System.Threading.Timer? _watchdog;
    private volatile int _lastMouseCbTick;
    private int _watchdogBusy;

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

    // Machine-switcher UX (Ctrl+Alt+←/→): arrows move the selection while the
    // modifiers stay held, releasing Ctrl or Alt commits the jump (Alt+Tab
    // style), Esc cancels. The UI shows a strip driven by these events.
    public event Action<string[], int>? SwitcherChanged;   // (machine ids left→right, selected index)
    public event Action? SwitcherClosed;
    /// <summary>A machine-switch jump landed: machineId is null for a local
    /// landing, else the remote machine; (x, y) are that machine's pixels.</summary>
    public event Action<string?, int, int>? JumpLanded;

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
        lock (_lifecycle)
        {
            if (IsRunning) return;
            var (x, y) = CursorInjector.GetPosition();
            lock (_sync)
            {
                _router.SeatLocal(x, y);
                _captured = false;
            }
            _ctrlHeld = _altHeld = false; // stale from a previous hook's lifetime
            _hook.Start();
            _kbHook.Start();
            _lastMouseCbTick = Environment.TickCount;
            _watchdog = new System.Threading.Timer(_ => WatchdogTick(), null, 2000, 2000);
            IsRunning = true;
        }
        RaiseStatus(force: true);
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            if (!IsRunning) return;
            _watchdog?.Dispose();
            _watchdog = null;
            _hook.Stop();
            _kbHook.Stop();
            _captured = false;
            CursorHider.Show(); // never leave the system cursor hidden
            IsRunning = false;
        }
        // The hook that would commit/cancel the switcher is gone — close the strip.
        _swMachines = null;
        SwitcherClosed?.Invoke();
        RaiseStatus(force: true);
    }

    /// <summary>
    /// Detects a mouse hook the OS silently removed while captured. Captured
    /// means the cursor is parked at screen centre and every real movement makes
    /// a hook callback that re-parks it — so "no callback for a while, yet the
    /// cursor wandered off the parking spot" can only mean the hook is gone.
    /// A user who simply isn't moving the mouse leaves the cursor at centre and
    /// never trips this. Heals by reinstalling the hook and yanking control
    /// home; without it the keyboard stays swallowed until the app restarts.
    /// </summary>
    private void WatchdogTick()
    {
        if (System.Threading.Interlocked.Exchange(ref _watchdogBusy, 1) != 0) return;
        try
        {
            if (!IsRunning || !_captured) return;
            if (Environment.TickCount - _lastMouseCbTick < 1500) return;
            var m = _metrics;
            var (cx, cy) = CursorInjector.GetPosition();
            if (Math.Abs(cx - m.CenterX) <= 8 && Math.Abs(cy - m.CenterY) <= 8) return;

            lock (_lifecycle)
            {
                if (!IsRunning || !_captured) return;
                _hook.Stop();
                _hook.Start();
                _lastMouseCbTick = Environment.TickCount;
            }
            ForceReleaseToLocal();
            Fault?.Invoke("마우스 훅이 시스템에서 제거되어 다시 설치했습니다 — 제어를 이 PC로 되돌렸습니다.");
        }
        finally { System.Threading.Volatile.Write(ref _watchdogBusy, 0); }
    }

    // Runs on the keyboard-hook thread — keep it fast. While captured, keys go
    // to the remote machine and are swallowed locally; when local, pass through.
    private int _ctrlTaps;
    private int _ctrlTapTick;

    private bool OnKeyEvent(KeyHookEvent e)
    {
        if (e.InjectedByUs) return false;

        // Track the PHYSICAL modifier state from the hook events themselves.
        // While captured, Ctrl/Alt are swallowed (forwarded to the remote), so
        // they never reach the OS key-state table — GetAsyncKeyState reports
        // them up and the machine-switch hotkey went dead exactly when the
        // cursor was on a remote machine. The hook observes every physical key
        // even when it swallows it, so this state works in both worlds.
        switch (e.Vk)
        {
            case 0x11 or 0xA2 or 0xA3: _ctrlHeld = !e.Up; break;
            case 0x12 or 0xA4 or 0xA5: _altHeld = !e.Up; break;
        }

        // Machine switch: Ctrl+Alt+←/→ opens the switcher strip and moves its
        // selection — works from local AND while captured. The jump itself
        // happens when a modifier is released (below), like Alt+Tab.
        if (!e.Up && e.Vk is 0x25 or 0x27 && CtrlAltHeld())
        {
            SwitcherAdvance(e.Vk == 0x27 ? +1 : -1);
            return true; // swallow — don't let the arrow reach apps
        }

        if (_swMachines is not null)
        {
            if (!e.Up && e.Vk == 0x1B) { CancelSwitcher(); return true; } // Esc cancels
            if (e.Up && e.Vk is 0x11 or 0xA2 or 0xA3 or 0x12 or 0xA4 or 0xA5)
            {
                CommitSwitcher();
                return _captured; // swallow like other keys while captured; pass through locally
            }
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

    private volatile bool _ctrlHeld, _altHeld;

    // Hook-tracked state covers the captured case (swallowed keys never reach
    // GetAsyncKeyState); the async fallback covers keys already held before the
    // hook started (e.g. right after engine start / a watchdog reinstall).
    private bool CtrlAltHeld() =>
        (_ctrlHeld || GetAsyncKeyState(0x11) < 0) && (_altHeld || GetAsyncKeyState(0x12) < 0);

    // Switcher session — touched only on the keyboard-hook thread.
    private List<string>? _swMachines;
    private int _swSel;

    /// <summary>Opens the switcher (first arrow press) or moves its selection,
    /// ordered left→right by each machine's leftmost monitor on the plane.</summary>
    private void SwitcherAdvance(int dir)
    {
        if (_swMachines is null)
        {
            List<string> machines;
            // TryEnter, not lock: this runs on the keyboard-hook thread. If the
            // engine is wedged, blocking here stalls every keystroke in the
            // system until Windows removes the keyboard hook.
            if (!Monitor.TryEnter(_sync, 100)) return;
            try
            {
                machines = _layout.Monitors
                    .GroupBy(m => m.MachineId)
                    .OrderBy(g => g.Min(m => m.PhysicalBounds.XMm))
                    .Select(g => g.Key)
                    .ToList();
            }
            finally { Monitor.Exit(_sync); }
            if (machines.Count < 2) return;
            _swMachines = machines;
            var current = _router.Active?.MachineId ?? _localMachineId;
            _swSel = Math.Max(0, machines.IndexOf(current));
        }
        _swSel = (_swSel + dir + _swMachines.Count) % _swMachines.Count;
        SwitcherChanged?.Invoke(_swMachines.ToArray(), _swSel);
    }

    private void CancelSwitcher()
    {
        if (_swMachines is null) return;
        _swMachines = null;
        SwitcherClosed?.Invoke();
    }

    private void CommitSwitcher()
    {
        var machines = _swMachines;
        if (machines is null) return;
        var target = machines[_swSel];
        _swMachines = null;
        SwitcherClosed?.Invoke(); // hide the strip on the OLD active machine before routing retargets
        if (_captured)
        {
            // The remote saw Ctrl/Alt go down before the session opened; the
            // committing releases are swallowed here, so release them there or
            // they stay held on that machine forever.
            RemoteKey?.Invoke(0xA2, 0x1D, false, false); // LCtrl up
            RemoteKey?.Invoke(0xA3, 0x1D, false, true);  // RCtrl up
            RemoteKey?.Invoke(0xA4, 0x38, false, false); // LAlt up
            RemoteKey?.Invoke(0xA5, 0x38, false, true);  // RAlt up
        }
        JumpToMachine(target);
        if (_captured)
        {
            // Landed remote: the user's physical Ctrl/Alt releases get swallowed
            // (captured keys are), so THIS machine would keep its modifiers held
            // forever. Inject the ups locally — no-ops if already released.
            CursorInjector.KeyEvent(0xA2, 0x1D, false, false);
            CursorInjector.KeyEvent(0xA3, 0x1D, false, true);
            CursorInjector.KeyEvent(0xA4, 0x38, false, false);
            CursorInjector.KeyEvent(0xA5, 0x38, false, true);
        }
    }

    /// <summary>Warps the cursor to the centre of a machine's largest monitor —
    /// the fast path across a wide plane, no seam-pushing required.
    ///
    /// Like <see cref="OnMove"/>: decide under the lock, notify after releasing
    /// it. Handlers reach into the network layer, which takes its own locks —
    /// holding _sync across them is a lock-order inversion.</summary>
    public void JumpToMachine(string machineId)
    {
        string? entered = null;
        var returned = false;
        var show = false; var hide = false;
        var warp = false; int wx = 0, wy = 0;
        int px = 0, py = 0;
        // TryEnter: callers include the keyboard-hook thread (Ctrl+Alt+arrows)
        // and the UI thread — a wedged engine must cost them a missed jump, not
        // a hang.
        if (!Monitor.TryEnter(_sync, 250)) return;
        try
        {
            var res = _router.JumpToMachine(machineId);
            if (res.Owner is null) return;
            (px, py) = res.PixelInt;

            if (res.Owner.MachineId == _localMachineId)
            {
                // Land on a local monitor: release capture (if any) and warp there.
                if (_captured)
                {
                    _captured = false;
                    show = true;
                    returned = true;
                }
                warp = true; wx = px; wy = py;
            }
            else
            {
                // Land on a remote monitor: park + capture, like a seam crossing.
                if (!_captured)
                {
                    _captured = true;
                    hide = HideCursorWhileCaptured;
                    var m = _metrics;
                    warp = true; wx = m.CenterX; wy = m.CenterY;
                }
                entered = res.Owner.MachineId;
            }
        }
        finally { Monitor.Exit(_sync); }

        if (show) CursorHider.Show();
        if (hide) CursorHider.Hide();
        if (warp) CursorInjector.WarpTo(wx, wy);
        if (returned) ControlReturnedLocal?.Invoke();
        if (entered is not null)
        {
            ControlEnteredRemote?.Invoke(entered); // retargets routing + Enter msg
            RemoteCursor?.Invoke(entered, px, py);
        }
        JumpLanded?.Invoke(entered, px, py); // after RemoteCursor so the warp lands first
        RaiseStatus(force: true);
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
        // This is the escape hatch — it runs on the keyboard-hook thread (panic
        // taps) and the watchdog, exactly when the engine may be wedged. Restore
        // what the user needs without touching _sync: keys and buttons check
        // _captured lock-free, so flipping it un-swallows input immediately, and
        // Show() makes the cursor visible again. The router reseat and the warp
        // go to a helper thread where a held _sync can't take the caller hostage.
        _captured = false;
        CursorHider.Show();
        var t = new Thread(() =>
        {
            int cx, cy;
            lock (_sync)
            {
                var m = _metrics;
                cx = m.CenterX; cy = m.CenterY;
                _router.SeatLocal(cx, cy);
            }
            CursorInjector.WarpTo(cx, cy);
            ControlReturnedLocal?.Invoke();
            RaiseStatus(force: true);
        }) { IsBackground = true, Name = "Snapfield.Release" };
        t.Start();
    }

    // Runs on the hook thread — keep it fast.
    private bool OnMouseEvent(MouseHookEvent e)
    {
        _lastMouseCbTick = Environment.TickCount; // any callback proves the hook is alive
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

    // SendInput pumps sent messages while it waits on the system's other
    // low-level hooks, so a REAL move can re-enter this callback on this same
    // thread while the outer call is still inside WarpTo (seen in a hang dump:
    // OnMove → SendInput → OnMove → SendInput …). Recursing injects yet again
    // and the nesting spirals until the hook times out — swallow re-entrant
    // moves instead; the next top-level move re-syncs the position.
    // [ThreadStatic] because a watchdog reinstall can briefly leave an old,
    // still-unwinding hook thread alive next to the new one.
    [ThreadStatic] private static bool _inMove;

    private bool OnMove(int x, int y)
    {
        if (_inMove) return _captured; // swallow while captured, else pass through
        _inMove = true;
        try { return OnMoveCore(x, y); }
        finally { _inMove = false; }
    }

    // Decide under _sync; warp/hide/show/notify only after releasing it.
    // Event handlers reach into the network layer, which takes its own locks —
    // running those under _sync inverted the session's lock order. And WarpTo
    // (SendInput) can block on every other hook in the system — holding _sync
    // across it parks the panic release and the watchdog behind a syscall that
    // may not return, which is exactly the captured-state brick this engine
    // must never reproduce.
    private bool OnMoveCore(int x, int y)
    {
        string? entered = null;   // capture just started on this machine
        string? streamTo = null;  // still remote: stream position to this machine
        var returned = false;     // capture just released back to local
        var show = false; var hide = false;
        var warp = false; int wx = 0, wy = 0;
        int px = 0, py = 0;

        lock (_sync)
        {
            if (!_captured)
            {
                var res = _router.OnLocalAbsolute(x, y);
                if (res.Transition != RouteTransition.ToRemote)
                    return false; // normal local movement

                // Crossing to a remote: park the cursor and capture.
                _captured = true;
                hide = HideCursorWhileCaptured;
                var m0 = _metrics;
                warp = true; wx = m0.CenterX; wy = m0.CenterY;
                if (res.Owner is not null)
                {
                    entered = res.Owner.MachineId;
                    streamTo = res.Owner.MachineId; // seed remote cursor at entry point
                    (px, py) = res.PixelInt;
                }
            }
            else
            {
                // Captured: convert the delta from the parked centre into physical mm.
                var m = _metrics;
                var dxMm = (x - m.CenterX) * m.MmPerPx * Sensitivity;
                var dyMm = (y - m.CenterY) * m.MmPerPx * Sensitivity;
                var result = _router.OnDelta(dxMm, dyMm);

                if (result.Transition == RouteTransition.ToLocal)
                {
                    _captured = false;
                    show = true;
                    (px, py) = result.PixelInt;
                    warp = true; wx = px; wy = py;
                    returned = true;
                }
                else
                {
                    // Still remote: stream the position, then reset the parking
                    // point so we always have movement headroom locally.
                    if (result.Owner is not null)
                    {
                        streamTo = result.Owner.MachineId;
                        (px, py) = result.PixelInt;
                    }
                    warp = true; wx = m.CenterX; wy = m.CenterY;
                }
            }
        }

        if (hide) CursorHider.Hide();
        if (show) CursorHider.Show();
        if (warp) CursorInjector.WarpTo(wx, wy);
        if (entered is not null) ControlEnteredRemote?.Invoke(entered);
        if (streamTo is not null) RemoteCursor?.Invoke(streamTo, px, py);
        if (returned) ControlReturnedLocal?.Invoke();
        RaiseStatus(force: entered is not null || returned);
        return true; // the move was consumed (parked or warped)
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
