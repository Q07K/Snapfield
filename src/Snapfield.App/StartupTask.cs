using System.Diagnostics;
using Microsoft.Win32;

namespace Snapfield.App;

/// <summary>
/// Run-at-login registration. The app requires administrator elevation (see
/// app.manifest — UIPI would otherwise kill hooks/injection over elevated
/// windows), and the HKCU Run key silently skips exes that require elevation.
/// A Task Scheduler logon task with RunLevel=Highest starts the app elevated
/// WITHOUT a UAC prompt — the same pattern Microsoft PowerToys uses.
/// </summary>
public static class StartupTask
{
    private const string TaskName = "Snapfield";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Snapfield";

    public static bool IsEnabled => SchTasks($"/Query /TN \"{TaskName}\"") == 0;

    public static void Set(bool enable)
    {
        if (enable && Environment.ProcessPath is string exe)
            SchTasks($"/Create /F /SC ONLOGON /RL HIGHEST /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\"");
        else if (!enable)
            SchTasks($"/Delete /F /TN \"{TaskName}\"");
    }

    /// <summary>
    /// One-time upgrade path: releases before elevation used an HKCU Run entry,
    /// which Windows now skips (it won't launch an exe that requires admin).
    /// Carry the user's autostart choice over to the scheduler task.
    /// </summary>
    public static void MigrateFromRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) is null) return;
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            Set(true);
        }
        catch { /* best-effort */ }
    }

    private static int SchTasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            if (p is null) return -1;
            if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
