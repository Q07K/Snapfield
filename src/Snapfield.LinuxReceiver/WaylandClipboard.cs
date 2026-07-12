using System.Diagnostics;
using System.Security.Cryptography;

namespace Snapfield.LinuxReceiver;

/// <summary>
/// Clipboard sync through wl-clipboard (`wl-copy` / `wl-paste`) — the portable
/// way to reach the Wayland clipboard from outside a toolkit. A long-lived
/// `wl-paste --watch` child signals every clipboard change; on each signal the
/// current content is fetched and shipped, with the same echo guards as the
/// desktop and Android receivers (never bounce back what the PC just sent).
/// Degrades to PC→Linux only (and warns) when wl-clipboard isn't installed.
/// </summary>
public sealed class WaylandClipboard : IDisposable
{
    private const int TextCap = 500_000;
    private const int PngCap = 8 * 1024 * 1024;

    public event Action<string>? TextChanged;
    public event Action<byte[]>? ImageChanged;
    public event Action<string>? Status;

    private volatile string _lastApplied = "";  // last text the PC sent us
    private volatile string _lastSent = "";     // last text we shipped to the PC
    private volatile string _lastImageMd5 = ""; // last image either direction
    private Process? _watcher;
    private volatile bool _disposed;

    public void Start()
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
                    OnChanged();
                }
            }
            catch { /* watcher died — sync just stops */ }
        }) { IsBackground = true, Name = "Snapfield.ClipWatch" };
        t.Start();
    }

    private void OnChanged()
    {
        try
        {
            var types = Run("wl-paste", "--list-types", out _) ?? "";
            if (types.Contains("image/png"))
            {
                Run("wl-paste", "--type image/png", out var png);
                if (png is null || png.Length == 0 || png.Length > PngCap) return;
                var md5 = Md5(png);
                if (md5 == _lastImageMd5) return; // our own apply, echoed back
                _lastImageMd5 = md5;
                ImageChanged?.Invoke(png);
                return;
            }
            if (!types.Contains("text/plain")) return;
            var text = Run("wl-paste", "--no-newline --type text/plain;charset=utf-8", out _);
            if (string.IsNullOrEmpty(text) || text.Length > TextCap) return;
            if (text == _lastApplied || text == _lastSent) return;
            _lastSent = text;
            TextChanged?.Invoke(text);
        }
        catch { /* fetch raced a clipboard change — the next signal catches up */ }
    }

    // ── PC → Linux ────────────────────────────────────────────────────────────
    public void Apply(string text)
    {
        _lastApplied = text;
        Pipe("wl-copy", "", System.Text.Encoding.UTF8.GetBytes(text));
    }

    public void ApplyPng(byte[] png)
    {
        _lastImageMd5 = Md5(png);
        Pipe("wl-copy", "--type image/png", png);
    }

    // ── plumbing ──────────────────────────────────────────────────────────────
    /// <summary>Run a command, return stdout as UTF-8 text AND raw bytes.</summary>
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
        catch { /* wl-copy missing — Start() already warned */ }
    }

    private static string Md5(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes));

    public void Dispose()
    {
        _disposed = true;
        try { _watcher?.Kill(entireProcessTree: true); } catch { }
    }
}
