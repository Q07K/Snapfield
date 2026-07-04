using System.Runtime.InteropServices;
using static Snapfield.Platform.Input.InputInterop;

namespace Snapfield.Platform.Input;

/// <summary>One observed keyboard event from the low-level hook.</summary>
public readonly record struct KeyHookEvent(int Vk, int Scan, bool Up, bool Extended, bool InjectedByUs);

/// <summary>
/// Installs a WH_KEYBOARD_LL hook on a dedicated thread with its own message
/// loop, mirroring <see cref="LowLevelMouseHook"/>. Return <c>true</c> from the
/// handler to swallow the event (used while control is on a remote machine).
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private readonly Func<KeyHookEvent, bool> _handler;
    private LowLevelMouseProc? _proc;    // same delegate shape: (nCode, wParam, lParam)
    private IntPtr _hook;
    private Thread? _thread;
    private uint _threadId;

    public LowLevelKeyboardHook(Func<KeyHookEvent, bool> handler) => _handler = handler;

    /// <summary>The hook could not be installed (raised on the hook thread).</summary>
    public event Action<string>? Failed;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(RunHookThread) { IsBackground = true, Name = "Snapfield.KeyboardHook" };
        _thread.Start();
    }

    private void RunHookThread()
    {
        _threadId = GetCurrentThreadId();
        _proc = HookCallback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            // Never throw here: an unhandled exception on this background
            // thread would terminate the whole process.
            Failed?.Invoke($"SetWindowsHookEx(WH_KEYBOARD_LL) failed (error {Marshal.GetLastWin32Error()}).");
            return;
        }

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
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var evt = new KeyHookEvent(
                (int)data.vkCode,
                (int)data.scanCode,
                (data.flags & LLKHF_UP) != 0,
                (data.flags & LLKHF_EXTENDED) != 0,
                data.dwExtraInfo == SnapfieldSignature);
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
