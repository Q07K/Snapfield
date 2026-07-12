using System.Diagnostics;
using System.Text.RegularExpressions;
using Snapfield.Core.Persistence;

namespace Snapfield.LinuxReceiver;

/// <summary>One physical output as the receiver advertises it to the controller.</summary>
public sealed record Screen(string Name, int X, int Y, int W, int H, double MmW, double MmH, bool Primary);

/// <summary>
/// Screen detection for a Wayland desktop. XWayland's xrandr view mirrors the
/// compositor's layout (exactly at 100% scale), and Ubuntu desktop always ships
/// XWayland — so parse `xrandr --current` instead of speaking each compositor's
/// private DBus dialect. Overridable from the command line when detection is
/// wrong or the session is headless.
/// </summary>
public static class ScreenInfo
{
    // e.g. "XWAYLAND0 connected primary 2560x1440+0+0 (normal ...) 597mm x 336mm"
    private static readonly Regex Line = new(
        @"^(\S+) connected (primary )?(\d+)x(\d+)\+(\d+)\+(\d+).*?(?:(\d+)mm x (\d+)mm)?\s*$",
        RegexOptions.Compiled);

    public static List<Screen> Detect(string? overrideSize, string? overrideMm)
    {
        if (overrideSize is not null)
        {
            var (w, h) = ParsePair(overrideSize, 'x');
            var (mmW, mmH) = overrideMm is not null ? ParsePair(overrideMm, 'x') : EstimateMm(w, h);
            return new List<Screen> { new("manual", 0, 0, w, h, mmW, mmH, true) };
        }

        var screens = new List<Screen>();
        try
        {
            var psi = new ProcessStartInfo("xrandr", "--current")
            { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            foreach (var raw in output.Split('\n'))
            {
                var m = Line.Match(raw.TrimEnd());
                if (!m.Success) continue;
                var w = int.Parse(m.Groups[3].Value);
                var h = int.Parse(m.Groups[4].Value);
                if (w == 0 || h == 0) continue;
                double mmW = m.Groups[7].Success ? int.Parse(m.Groups[7].Value) : 0;
                double mmH = m.Groups[8].Success ? int.Parse(m.Groups[8].Value) : 0;
                if (mmW < 10 || mmH < 10) (mmW, mmH) = EstimateMm(w, h); // panels occasionally lie
                screens.Add(new Screen(
                    m.Groups[1].Value,
                    int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value),
                    w, h, mmW, mmH, m.Groups[2].Success));
            }
        }
        catch { /* no xrandr / no DISPLAY — fall through */ }

        if (screens.Count == 0)
        {
            Console.WriteLine("경고: 화면을 감지하지 못했습니다 (xrandr 실패) — 1920x1080으로 가정합니다. --size WxH 로 지정하세요.");
            var (mmW, mmH) = EstimateMm(1920, 1080);
            screens.Add(new Screen("fallback", 0, 0, 1920, 1080, mmW, mmH, true));
        }
        return screens;
    }

    /// <summary>The pixel bounding box all controller coordinates live in.</summary>
    public static (int X, int Y, int W, int H) Union(List<Screen> screens)
    {
        int l = screens.Min(s => s.X), t = screens.Min(s => s.Y);
        int r = screens.Max(s => s.X + s.W), b = screens.Max(s => s.Y + s.H);
        return (l, t, r - l, b - t);
    }

    public static MonitorState[] AsMonitors(List<Screen> screens, string machineId)
    {
        return screens.Select(s =>
        {
            // Physical placement mirrors the pixel arrangement so multi-monitor
            // receivers land on the controller's plane in the right shape; the
            // desktop's true-size correction can refine it afterwards.
            var mmPerPx = s.MmW / s.W;
            return new MonitorState
            {
                MachineId = machineId,
                DeviceId = $"linux-{s.Name}",
                DisplayName = s.Name,
                PixelLeft = s.X,
                PixelTop = s.Y,
                PixelWidth = s.W,
                PixelHeight = s.H,
                PhysicalXMm = s.X * mmPerPx,
                PhysicalYMm = s.Y * mmPerPx,
                PhysicalWidthMm = s.MmW,
                PhysicalHeightMm = s.MmH,
                DpiScale = 1.0,
                IsInternal = s.Primary,
                Kind = 1, // monitor
            };
        }).ToArray();
    }

    private static (int, int) ParsePair(string s, char sep)
    {
        var parts = s.Split(sep, 'X', ',');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b) || a <= 0 || b <= 0)
            throw new ArgumentException($"크기 형식이 잘못됐습니다: '{s}' (예: 2560x1440)");
        return (a, b);
    }

    private static (double, double) EstimateMm(int w, int h) => (w / 96.0 * 25.4, h / 96.0 * 25.4);
}
