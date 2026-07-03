using System.Runtime.InteropServices;

namespace Snapfield.Platform.Net;

/// <summary>Raw Win32 clipboard text/image access — no STA thread or window required.</summary>
public static class ClipboardIO
{
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIB = 8;
    private const uint CF_HDROP = 15;
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

    /// <summary>Reads a clipboard bitmap (CF_DIB) and returns it PNG-encoded.</summary>
    public static bool TryGetImagePng(out byte[] png)
    {
        png = Array.Empty<byte>();
        if (!IsClipboardFormatAvailable(CF_DIB)) return false;
        if (!OpenWithRetry()) return false;
        byte[] dib;
        try
        {
            var handle = GetClipboardData(CF_DIB);
            if (handle == IntPtr.Zero) return false;
            var size = (int)(ulong)GlobalSize(handle);
            if (size <= 40) return false;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) return false;
            try
            {
                dib = new byte[size];
                Marshal.Copy(ptr, dib, 0, size);
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }

        try
        {
            // Wrap the DIB in a BITMAPFILEHEADER so GDI+ can parse it as a .bmp,
            // then re-encode as PNG (a raw 4K DIB is ~33 MB; PNG is wire-friendly).
            var biSize = BitConverter.ToInt32(dib, 0);
            int bitCount = BitConverter.ToInt16(dib, 14);
            var compression = BitConverter.ToInt32(dib, 16);
            var clrUsed = BitConverter.ToInt32(dib, 32);
            var palette = bitCount <= 8 ? (clrUsed == 0 ? 1 << bitCount : clrUsed) * 4 : 0;
            var masks = compression == 3 && biSize == 40 ? 12 : 0; // BI_BITFIELDS
            var offBits = 14 + biSize + masks + palette;

            using var bmpStream = new MemoryStream(14 + dib.Length);
            using var w = new BinaryWriter(bmpStream);
            w.Write((ushort)0x4D42); // "BM"
            w.Write(14 + dib.Length);
            w.Write(0);
            w.Write(offBits);
            w.Write(dib);
            bmpStream.Position = 0;

            using var bitmap = new System.Drawing.Bitmap(bmpStream);
            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
            png = pngStream.ToArray();
            return png.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>Decodes a PNG and places it on the clipboard as CF_DIB.</summary>
    public static bool TrySetImagePng(byte[] png)
    {
        byte[] bmpBytes;
        try
        {
            using var src = new MemoryStream(png);
            using var bitmap = new System.Drawing.Bitmap(src);
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
            bmpBytes = ms.ToArray(); // BITMAPFILEHEADER(14) + DIB
        }
        catch { return false; }

        var dibLen = bmpBytes.Length - 14;
        if (dibLen <= 0) return false;
        if (!OpenWithRetry()) return false;
        try
        {
            EmptyClipboard();
            var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)dibLen);
            if (handle == IntPtr.Zero) return false;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) { GlobalFree(handle); return false; }
            try { Marshal.Copy(bmpBytes, 14, ptr, dibLen); }
            finally { GlobalUnlock(handle); }

            if (SetClipboardData(CF_DIB, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }
            return true;
        }
        finally { CloseClipboard(); }
    }

    public static bool IsFileListAvailable() => IsClipboardFormatAvailable(CF_HDROP);

    /// <summary>Reads the full paths of files copied in Explorer (CF_HDROP).</summary>
    public static bool TryGetFilePaths(out string[] paths)
    {
        paths = Array.Empty<string>();
        if (!IsClipboardFormatAvailable(CF_HDROP)) return false;
        if (!OpenWithRetry()) return false;
        try
        {
            var hDrop = GetClipboardData(CF_HDROP);
            if (hDrop == IntPtr.Zero) return false;
            var count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            if (count == 0) return false;
            var list = new List<string>((int)count);
            var sb = new System.Text.StringBuilder(260);
            for (uint i = 0; i < count; i++)
            {
                sb.Clear(); sb.EnsureCapacity(260);
                var len = DragQueryFile(hDrop, i, sb, 260);
                if (len > 0) list.Add(sb.ToString());
            }
            paths = list.ToArray();
            return paths.Length > 0;
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Places a set of local files on the clipboard as CF_HDROP (paste-ready).</summary>
    public static bool TrySetFiles(string[] paths)
    {
        if (paths.Length == 0) return false;

        // DROPFILES header (20 bytes) + double-null-terminated wide path list.
        var listChars = paths.Sum(p => p.Length + 1) + 1; // each path + null, plus final null
        var size = 20 + listChars * 2;

        if (!OpenWithRetry()) return false;
        try
        {
            EmptyClipboard();
            var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)size);
            if (handle == IntPtr.Zero) return false;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) { GlobalFree(handle); return false; }
            try
            {
                Marshal.WriteInt32(ptr, 0, 20);   // pFiles: offset to the list
                Marshal.WriteInt32(ptr, 4, 0);    // pt.x
                Marshal.WriteInt32(ptr, 8, 0);    // pt.y
                Marshal.WriteInt32(ptr, 12, 0);   // fNC
                Marshal.WriteInt32(ptr, 16, 1);   // fWide = Unicode
                var offset = 20;
                foreach (var p in paths)
                {
                    var bytes = System.Text.Encoding.Unicode.GetBytes(p + "\0");
                    Marshal.Copy(bytes, 0, ptr + offset, bytes.Length);
                    offset += bytes.Length;
                }
                Marshal.WriteInt16(ptr, offset, 0); // final terminator
            }
            finally { GlobalUnlock(handle); }

            if (SetClipboardData(CF_HDROP, handle) == IntPtr.Zero) { GlobalFree(handle); return false; }
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
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, System.Text.StringBuilder? lpszFile, uint cch);
}

/// <summary>
/// Watches the clipboard by polling <c>GetClipboardSequenceNumber</c> (no window
/// or hook needed) and raises <see cref="TextChanged"/> for new text content.
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int PollMs = 400;
    private const int MaxTextLength = 500_000;      // keep frames reasonable
    private const int MaxImageBytes = 8_000_000;    // PNG; PeerLink frames cap at 16 MB

    private Thread? _thread;
    private volatile bool _stop;
    private long _ignoreUpToSeq = -1;

    public event Action<string>? TextChanged;
    public event Action<byte[]>? ImageChanged;        // PNG bytes
    public event Action<string[]>? FilesChanged;      // full local paths

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Snapfield.Clipboard" };
        _thread.Start();
    }

    /// <summary>
    /// Call right after THIS process writes the clipboard (applying a peer's
    /// copy): every change up to the current sequence number is ours and must
    /// not be echoed back. Format-agnostic — covers text and images alike.
    /// </summary>
    public void NoteSelfChange() =>
        Interlocked.Exchange(ref _ignoreUpToSeq, GetClipboardSequenceNumber());

    private void Run()
    {
        var last = GetClipboardSequenceNumber();
        while (!_stop)
        {
            Thread.Sleep(PollMs);
            var now = GetClipboardSequenceNumber();
            if (now == last) continue;
            last = now;
            var ignoreUpTo = Interlocked.Read(ref _ignoreUpToSeq);
            if (ignoreUpTo >= 0 && now <= (uint)ignoreUpTo) continue; // our own write

            // Files (Explorer copy) are the most specific; then text (Office puts
            // text + bitmap together, so text beats image); then image.
            if (ClipboardIO.IsFileListAvailable())
            {
                if (ClipboardIO.TryGetFilePaths(out var paths)) FilesChanged?.Invoke(paths);
            }
            else if (ClipboardIO.TryGetText(out var text))
            {
                if (text.Length <= MaxTextLength) TextChanged?.Invoke(text);
            }
            else if (ClipboardIO.TryGetImagePng(out var png))
            {
                if (png.Length <= MaxImageBytes) ImageChanged?.Invoke(png);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread = null;
    }

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}
