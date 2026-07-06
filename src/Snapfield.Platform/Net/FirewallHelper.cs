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

    private static string? ShowRule()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall show rule name=\"{RuleName}\" verbose",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output;
        }
        catch { return null; }
    }

    /// <summary>Some rule with our name exists (whichever exe it points at).
    /// Locale-safe: the error text never contains the rule name.</summary>
    private static bool RuleNameExists(string? output) =>
        output is not null && output.Contains(RuleName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True only when the rule covers THIS executable. The rule is program-scoped
    /// and every release ships a differently-named exe, so the old name-only check
    /// kept "confirming" a rule that pointed at the previous version — silently
    /// killing inbound UDP discovery (and the TCP listen) after each update.
    /// </summary>
    public static bool RuleExists()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        var output = ShowRule();
        return RuleNameExists(output) && output!.Contains(exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Makes sure an inbound allow rule exists FOR THE CURRENT exe (one UAC
    /// consent). A stale rule left by a previous exe path is replaced within the
    /// same elevation. Returns a short status string for the UI.
    /// </summary>
    public static string EnsureRule()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return "실행 파일 경로를 찾지 못해 방화벽 등록을 건너뜁니다.";
        if (RuleExists()) return "방화벽 규칙 확인됨.";

        try
        {
            // Delete-then-add inside ONE elevated cmd, so a stale rule (old exe
            // path) is replaced with a single UAC consent; the delete is a no-op
            // when no rule exists yet.
            var del = $"netsh advfirewall firewall delete rule name=\"{RuleName}\"";
            var add = $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow enable=yes profile=any program=\"{exe}\"";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {del} & {add}",
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
