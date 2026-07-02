using System.Diagnostics;

namespace Snapfield.Platform.Net;

/// <summary>
/// Ensures an inbound Windows-firewall rule exists for this executable so the
/// receiver can accept connections without the user hand-editing firewall
/// settings (the Mouse-Without-Borders approach: register a program rule once,
/// with a single UAC consent).
/// </summary>
public static class FirewallHelper
{
    private const string RuleName = "Snapfield";

    public static bool RuleExists()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall show rule name=\"{RuleName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output.Contains(RuleName, StringComparison.OrdinalIgnoreCase)
                   && !output.Contains("No rules match", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Adds a program-scoped inbound allow rule (elevating once via UAC) unless it
    /// already exists. Returns a short status string for the UI.
    /// </summary>
    public static string EnsureRule()
    {
        if (RuleExists()) return "방화벽 규칙 확인됨.";

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return "실행 파일 경로를 찾지 못해 방화벽 등록을 건너뜁니다.";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow enable=yes profile=any program=\"{exe}\"",
                UseShellExecute = true,
                Verb = "runas", // one UAC consent
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
            return RuleExists() ? "방화벽 규칙을 등록했습니다." : "방화벽 등록이 완료되지 않았습니다 (연결 시 Windows 허용 팝업이 뜰 수 있음).";
        }
        catch
        {
            // User declined UAC — the standard Windows firewall prompt still works.
            return "방화벽 등록을 건너뜀 (연결 시 Windows 허용 팝업에서 '허용'을 누르세요).";
        }
    }
}
