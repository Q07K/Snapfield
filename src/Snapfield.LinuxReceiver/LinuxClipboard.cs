using System.Diagnostics;
using System.Security.Cryptography;

namespace Snapfield.LinuxReceiver;

/// <summary>
/// Clipboard sync with two backends, picked by session type:
///  - Wayland: wl-clipboard (`wl-copy` / `wl-paste`). A long-lived
///    `wl-paste --watch` child signals every clipboard change.
///  - X11: xclip. X11 has no change notification a CLI can subscribe to,
///    so a 1s poll compares content against the last state instead.
/// Both directions share the same echo guards as the desktop and Android
/// receivers (never bounce back what the PC just sent). Degrades to
/// PC→Linux only (and warns) when the backend tool isn't installed.
/// </summary>
public sealed class LinuxClipboard : IDisposable
{
    private const int TextCap = 500_000;
    private const int PngCap = 8 * 1024 * 1024;

    /// <summary>True when the desktop session is X11 (no Wayland display) —
    /// NVIDIA-driver setups often run GNOME on X11 even on modern Ubuntu.</summary>
    public static bool SessionIsX11 =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    public event Action<string>? TextChanged;
    public event Action<byte[]>? ImageChanged;
    public event Action<string>? Status;

    private readonly bool _x11 = SessionIsX11;
    private volatile string _lastApplied = "";  // last text the PC sent us
    private volatile string _lastSent = "";     // last text we shipped to the PC
    private volatile string _lastImageMd5 = ""; // last image either direction
    private Process? _watcher;
    private volatile bool _disposed;

    public void Start()
    {
        if (_x11) StartX11Poll();
        else StartWaylandWatch();
    }

    private void StartWaylandWatch()
    {
        try
        {
            // `--watch echo x` prints one line per clipboard change: a change
            // SIGNAL we can read forever, without inheriting the ambiguity of
            // concatenated clipboard payloads on one stream.
            var psi = new ProcessStartInfo("wl-paste", "--watch echo x")
            { RedirectStandardOutput = true, RedirectStandardError = true };
            _watcher = Process.Start(psi);
        }
        catch
        {
            Status?.Invoke("wl-clipboard가 없습니다 (sudo apt install wl-clipboard) — 클립보드 동기화 꺼짐.");
            return;
        }

        var t = new Thread(() =>
        {
            try
            {
                // Swallow the initial snapshot event: syncing whatever happened
                // to be on the clipboard before we started would surprise.
                var first = true;
                while (!_disposed && _watcher!.StandardOutput.ReadLine() is not null)
                {
                    if (first) { first = false; continue; }
                    try { Sync(notify: true); } catch { /* raced a change — next signal catches up */ }
                }
            }
            catch { /* watcher died — sync just stops */ }
        }) { IsBackground = true, Name = "Snapfield.ClipWatch" };
        t.Start();
    }

    private void StartX11Poll()
    {
        // Probe once so a missing xclip warns immediately instead of the poll
        // thread dying silently.
        if (Run("xclip", "-version", out _) is null)
        {
            Status?.Invoke("xclip이 없습니다 (sudo apt install xclip) — 클립보드 동기화 꺼짐.");
            return;
        }
        var t = new Thread(() =>
        {
            var first = true;
            while (!_disposed)
            {
                // First pass primes the guards with whatever was already on the
                // clipboard, so pre-existing content isn't synced by surprise.
                try { Sync(notify: !first); } catch { }
                first = false;
                Thread.Sleep(1000);
            }
        }) { IsBackground = true, Name = "Snapfield.ClipPoll" };
        t.Start();
    }

    /// <summary>Fetch the clipboard and ship it if it changed. notify=false
    /// records the current content without firing events.</summary>
    private void Sync(bool notify)
    {
        var types = (_x11
            ? Run("xclip", "-selection clipboard -t TARGETS -o", out _)
            : Run("wl-paste", "--list-types", out _)) ?? "";
        if (types.Contains("image/png"))
        {
            var png = _x11 ? RunBytes("xclip", "-selection clipboard -t image/png -o")
                           : RunBytes("wl-paste", "--type image/png");
            if (png is null || png.Length == 0 || png.Length > PngCap) return;
            var md5 = Md5(png);
            if (md5 == _lastImageMd5) return; // unchanged, or our own apply echoed back
            _lastImageMd5 = md5;
            if (notify) ImageChanged?.Invoke(png);
            return;
        }
        var hasText = _x11
            ? types.Contains("STRING") || types.Contains("text/plain") // UTF8_STRING contains STRING
            : types.Contains("text/plain");
        if (!hasText) return;
        var text = _x11
            ? Run("xclip", "-selection clipboard -t UTF8_STRING -o", out _)
            : Run("wl-paste", "--no-newline --type text/plain;charset=utf-8", out _);
        if (string.IsNullOrEmpty(text) || text.Length > TextCap) return;
        if (text == _lastApplied || text == _lastSent) return;
        _lastSent = text;
        if (notify) TextChanged?.Invoke(text);
    }

    // ── PC → Linux ────────────────────────────────────────────────────────────
    public void Apply(string text)
    {
        _lastApplied = text;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        if (_x11) Pipe("xclip", "-selection clipboard -in", bytes);
        else Pipe("wl-copy", "", bytes);
    }

    public void ApplyPng(byte[] png)
    {
        _lastImageMd5 = Md5(png);
        if (_x11) Pipe("xclip", "-selection clipboard -t image/png -in", png);
        else Pipe("wl-copy", "--type image/png", png);
    }

    // ── plumbing ──────────────────────────────────────────────────────────────
    /// <summary>Run a command, return stdout as UTF-8 text AND raw bytes.
    /// Returns null when the binary is missing or the run failed.</summary>
    private static string? Run(string cmd, string args, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            var psi = new ProcessStartInfo(cmd, args)
            { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            using var mem = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(mem);
            p.WaitForExit(5000);
            bytes = mem.ToArray();
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }

    private static byte[]? RunBytes(string cmd, string args)
        => Run(cmd, args, out var bytes) is null ? null : bytes;

    private static void Pipe(string cmd, string args, byte[] stdin)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd, args)
            { RedirectStandardInput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.StandardInput.BaseStream.Write(stdin);
            p.StandardInput.Close();
            p.WaitForExit(5000);
        }
        catch { /* backend tool missing — Start() already warned */ }
    }

    private static string Md5(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes));

    public void Dispose()
    {
        _disposed = true;
        try { _watcher?.Kill(entireProcessTree: true); } catch { }
    }
}
