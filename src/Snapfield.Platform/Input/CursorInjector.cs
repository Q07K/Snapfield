using static Snapfield.Platform.Input.InputInterop;

namespace Snapfield.Platform.Input;

/// <summary>
/// Places the system cursor at absolute physical-pixel coordinates via SendInput.
/// All injected events carry <see cref="InputInterop.SnapfieldSignature"/> so the
/// hook can distinguish them from real mouse movement and avoid feedback loops.
/// </summary>
public static class CursorInjector
{
    private static readonly int InputSize = System.Runtime.InteropServices.Marshal.SizeOf<INPUT>();

    // SendInput wants an array; injection runs per mouse move on the hook and
    // network-reader threads, so reuse a one-element buffer per thread.
    [ThreadStatic] private static INPUT[]? _inputBuf;

    /// <summary>Virtual-screen bounds, cached — queried per move otherwise.</summary>
    private sealed record VirtualScreen(int Left, int Top, int Width, int Height);
    private static volatile VirtualScreen? _vScreen;

    static CursorInjector()
    {
        // Resolution / monitor topology changes invalidate the cached bounds.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => _vScreen = null;
    }

    public static (int X, int Y) GetPosition()
    {
        GetCursorPos(out var p);
        return (p.x, p.y);
    }

    /// <summary>Warps the cursor to a point in virtual-desktop pixels.
    /// Returns false when the OS rejected the injection (e.g. UIPI blocks a
    /// non-elevated process while an elevated window has the foreground).</summary>
    public static bool WarpTo(int x, int y)
    {
        var vs = _vScreen ??= new VirtualScreen(
            GetSystemMetrics(SM_XVIRTUALSCREEN),
            GetSystemMetrics(SM_YVIRTUALSCREEN),
            Math.Max(GetSystemMetrics(SM_CXVIRTUALSCREEN), 1),
            Math.Max(GetSystemMetrics(SM_CYVIRTUALSCREEN), 1));

        // Normalise to the 0..65535 absolute range SendInput expects.
        int nx = (int)Math.Round((x - vs.Left) * 65535.0 / Math.Max(vs.Width - 1, 1));
        int ny = (int)Math.Round((y - vs.Top) * 65535.0 / Math.Max(vs.Height - 1, 1));

        return Dispatch(new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = nx,
                    dy = ny,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    dwExtraInfo = SnapfieldSignature,
                },
            },
        });
    }

    /// <summary>Injects a mouse button transition at the current cursor position.</summary>
    public static void MouseButton(int button, bool down)
    {
        uint flags = button switch
        {
            0 => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            1 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            2 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => 0,
        };
        if (flags == 0) return;
        Send(flags, 0);
    }

    /// <summary>Injects a wheel scroll (vertical unless <paramref name="horizontal"/>).</summary>
    public static void Wheel(int delta, bool horizontal) =>
        Send(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, (uint)delta);

    /// <summary>Injects a keyboard transition (virtual key + scan code).</summary>
    public static void KeyEvent(int vk, int scan, bool down, bool extended)
    {
        uint flags = 0;
        if (!down) flags |= KEYEVENTF_KEYUP;
        if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
        Dispatch(new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = (ushort)scan,
                    dwFlags = flags,
                    dwExtraInfo = SnapfieldSignature,
                },
            },
        });
    }

    private static void Send(uint flags, uint mouseData)
    {
        Dispatch(new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dwFlags = flags, mouseData = mouseData, dwExtraInfo = SnapfieldSignature },
            },
        });
    }

    private static bool Dispatch(in INPUT input)
    {
        var buf = _inputBuf ??= new INPUT[1];
        buf[0] = input;
        return SendInput(1, buf, InputSize) == 1;
    }
}
