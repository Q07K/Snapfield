using System.Runtime.InteropServices;
using static Snapfield.Platform.Input.InputInterop;

namespace Snapfield.Platform.Input;

/// <summary>One observed mouse event from the low-level hook.</summary>
public readonly record struct MouseHookEvent(int Message, int X, int Y, uint MouseData, bool InjectedByUs);

/// <summary>
/// Installs a WH_MOUSE_LL hook on a dedicated thread with its own message loop.
/// The handler runs on that thread and MUST return quickly (Windows silently drops
/// a hook whose callback is too slow). Return <c>true</c> from the handler to
/// swallow the event so it never reaches the rest of the system (used while the
/// cursor is "captured" on a remote machine).
/// </summary>
public sealed class LowLevelMouseHook : IDisposable
{
    private readonly Func<MouseHookEvent, bool> _handler;
    private LowLevelMouseProc? _proc;    // kept alive to prevent GC of the callback
    private IntPtr _hook;
    private Thread? _thread;
    private uint _threadId;

    public LowLevelMouseHook(Func<MouseHookEvent, bool> handler) => _handler = handler;

    /// <summary>The hook could not be installed (raised on the hook thread).</summary>
    public event Action<string>? Failed;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(RunHookThread) { IsBackground = true, Name = "Snapfield.MouseHook" };
        _thread.Start();
    }

    private void RunHookThread()
    {
        _threadId = GetCurrentThreadId();
        _proc = HookCallback;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            // Never throw here: an unhandled exception on this background
            // thread would terminate the whole process.
            Failed?.Invoke($"SetWindowsHookEx(WH_MOUSE_LL) failed (error {Marshal.GetLastWin32Error()}).");
            return;
        }

        // Pump messages so the hook stays alive; exits when WM_QUIT is posted.
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var ours = data.dwExtraInfo == SnapfieldSignature;
            var evt = new MouseHookEvent((int)wParam, data.pt.x, data.pt.y, data.mouseData, ours);
            try
            {
                if (_handler(evt))
                    return (IntPtr)1; // suppress
            }
            catch { /* never let a handler exception kill the hook */ }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Stop()
    {
        if (_thread is null) return;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
        _threadId = 0;
    }

    public void Dispose() => Stop();
}
