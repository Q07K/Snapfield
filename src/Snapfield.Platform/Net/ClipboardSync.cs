using System.Runtime.InteropServices;

namespace Snapfield.Platform.Net;

/// <summary>Raw Win32 clipboard text access — no STA thread or window required.</summary>
public static class ClipboardIO
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static bool TryGetText(out string text)
    {
        text = "";
        if (!OpenWithRetry()) return false;
        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return false;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) return false;
            try { text = Marshal.PtrToStringUni(ptr) ?? ""; }
            finally { GlobalUnlock(handle); }
            return text.Length > 0;
        }
        finally { CloseClipboard(); }
    }

    public static bool TrySetText(string text)
    {
        if (!OpenWithRetry()) return false;
        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (handle == IntPtr.Zero) return false;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) { GlobalFree(handle); return false; }
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * 2, 0); // null terminator
            }
            finally { GlobalUnlock(handle); }

            if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero)
            {
                GlobalFree(handle); // only free on failure — on success the system owns it
                return false;
            }
            return true;
        }
        finally { CloseClipboard(); }
    }

    private static bool OpenWithRetry()
    {
        // The clipboard is a shared, frequently-locked resource — retry briefly.
        for (var i = 0; i < 5; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(40);
        }
        return false;
    }

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);
}

/// <summary>
/// Watches the clipboard by polling <c>GetClipboardSequenceNumber</c> (no window
/// or hook needed) and raises <see cref="TextChanged"/> for new text content.
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int PollMs = 400;
    private const int MaxTextLength = 500_000; // keep frames reasonable

    private Thread? _thread;
    private volatile bool _stop;

    public event Action<string>? TextChanged;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Snapfield.Clipboard" };
        _thread.Start();
    }

    private void Run()
    {
        var last = GetClipboardSequenceNumber();
        while (!_stop)
        {
            Thread.Sleep(PollMs);
            var now = GetClipboardSequenceNumber();
            if (now == last) continue;
            last = now;
            if (ClipboardIO.TryGetText(out var text) && text.Length <= MaxTextLength)
                TextChanged?.Invoke(text);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread = null;
    }

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}
