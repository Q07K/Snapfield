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
}
