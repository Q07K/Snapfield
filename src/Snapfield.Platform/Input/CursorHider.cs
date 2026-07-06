using System.Runtime.InteropServices;

namespace Snapfield.Platform.Input;

/// <summary>
/// Hides the system cursor globally while control is on a remote machine, by
/// replacing the standard system cursors with a fully transparent one (the
/// Mouse-Without-Borders technique — ShowCursor() is per-thread and useless
/// here). Restoring reloads the user's cursor scheme, so nothing is leaked
/// even though SetSystemCursor destroys the handles we pass in.
/// </summary>
public static class CursorHider
{
    // OCR_* ids of every stock cursor the parked pointer could morph into.
    private static readonly uint[] CursorIds =
    {
        32512, // OCR_NORMAL
        32513, // OCR_IBEAM
        32514, // OCR_WAIT
        32515, // OCR_CROSS
        32516, // OCR_UP
        32642, // OCR_SIZENWSE
        32643, // OCR_SIZENESW
        32644, // OCR_SIZEWE
        32645, // OCR_SIZENS
        32646, // OCR_SIZEALL
        32648, // OCR_NO
        32649, // OCR_HAND
        32650, // OCR_APPSTARTING
        32651, // OCR_HELP
    };

    private static readonly object Gate = new();
    private static bool _hidden;

    public static void Hide()
    {
        lock (Gate)
        {
            if (_hidden) return;
            foreach (var id in CursorIds)
            {
                var blank = CreateBlankCursor();
                if (blank != IntPtr.Zero)
                    SetSystemCursor(blank, id); // consumes (destroys) the handle
            }
            _hidden = true;
        }
    }

    public static void Show()
    {
        lock (Gate)
        {
            if (!_hidden) return;
            // Reload the user's cursor scheme from the registry.
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
            _hidden = false;
        }
    }

    /// <summary>
    /// Restores the cursor scheme unconditionally — ignores the in-process flag.
    /// The transparent cursors are a SYSTEM-wide change that survives our process,
    /// so a crash/kill while captured strands the user with an invisible cursor;
    /// call this on startup (and from crash handlers) to heal that.
    /// </summary>
    public static void ForceRestore()
    {
        lock (Gate)
        {
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
            _hidden = false;
        }
    }

    private static IntPtr CreateBlankCursor()
    {
        const int w = 32, h = 32;
        var and = new byte[w * h / 8];
        var xor = new byte[w * h / 8];
        for (var i = 0; i < and.Length; i++) and[i] = 0xFF; // AND=1, XOR=0 -> transparent
        return CreateCursor(GetModuleHandle(null), 0, 0, w, h, and, xor);
    }

    private const uint SPI_SETCURSORS = 0x0057;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot,
        int nWidth, int nHeight, byte[] pvAndPlane, byte[] pvXorPlane);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
