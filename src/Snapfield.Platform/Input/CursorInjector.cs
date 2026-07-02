using static Snapfield.Platform.Input.InputInterop;

namespace Snapfield.Platform.Input;

/// <summary>
/// Places the system cursor at absolute physical-pixel coordinates via SendInput.
/// All injected events carry <see cref="InputInterop.SnapfieldSignature"/> so the
/// hook can distinguish them from real mouse movement and avoid feedback loops.
/// </summary>
public static class CursorInjector
{
    public static (int X, int Y) GetPosition()
    {
        GetCursorPos(out var p);
        return (p.x, p.y);
    }

    /// <summary>Warps the cursor to a point in virtual-desktop pixels.</summary>
    public static void WarpTo(int x, int y)
    {
        int vsLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vsTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vsWidth = Math.Max(GetSystemMetrics(SM_CXVIRTUALSCREEN), 1);
        int vsHeight = Math.Max(GetSystemMetrics(SM_CYVIRTUALSCREEN), 1);

        // Normalise to the 0..65535 absolute range SendInput expects.
        int nx = (int)Math.Round((x - vsLeft) * 65535.0 / Math.Max(vsWidth - 1, 1));
        int ny = (int)Math.Round((y - vsTop) * 65535.0 / Math.Max(vsHeight - 1, 1));

        var input = new INPUT
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
        };
        SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
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
        var input = new INPUT
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
        };
        SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    private static void Send(uint flags, uint mouseData)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dwFlags = flags, mouseData = mouseData, dwExtraInfo = SnapfieldSignature },
            },
        };
        SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }
}
