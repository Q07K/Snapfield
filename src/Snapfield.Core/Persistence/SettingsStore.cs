using System.Text.Json;

namespace Snapfield.Core.Persistence;

/// <summary>A remembered machine the user has controlled, for one-tap reconnect.</summary>
public sealed record RecentConnection
{
    public string Name { get; init; } = "";   // remote machine name, or host until known
    public string Host { get; init; } = "";
    public int Port { get; init; } = 45654;
    public string Pin { get; init; } = "";
}

/// <summary>App-level settings, persisted next to the layout file.</summary>
public sealed record AppSettings
{
    /// <summary>"Controller", "Receiver", or null when no session has run yet.</summary>
    public string? LastRole { get; init; }
    public string LastHost { get; init; } = "";
    public int LastPort { get; init; } = 45654;

    /// <summary>Re-start the last session automatically when the app launches.</summary>
    public bool RestoreOnLaunch { get; init; } = true;

    /// <summary>Pairing code this machine shows when acting as receiver (generated once).</summary>
    public string ReceiverPin { get; init; } = "";

    /// <summary>Pairing code last used to connect to a receiver.</summary>
    public string ControllerPin { get; init; } = "";

    /// <summary>Machines this PC has controlled, most-recent first.</summary>
    public List<RecentConnection> Recent { get; init; } = new();

    /// <summary>User-given display names, keyed by machine id (e.g. VICS_GYUHYEONG → 사무실 PC).</summary>
    public Dictionary<string, string> Nicknames { get; init; } = new();

    /// <summary>Last main-window size (0 = never saved; defaults apply).</summary>
    public double WindowWidth { get; init; }
    public double WindowHeight { get; init; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snapfield", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(DefaultPath), Options) ?? new AppSettings();
        }
        catch { /* corrupted settings -> defaults */ }
        return new AppSettings();
    }

    /// <summary>Adds or moves a connection to the front of the recent list (max 6, deduped by host).</summary>
    public static void RememberConnection(RecentConnection conn)
    {
        var s = Load();
        var recent = s.Recent.Where(r => !string.Equals(r.Host, conn.Host, StringComparison.OrdinalIgnoreCase)).ToList();
        recent.Insert(0, conn);
        if (recent.Count > 6) recent = recent.Take(6).ToList();
        Save(s with { Recent = recent });
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(DefaultPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(DefaultPath, JsonSerializer.Serialize(settings, Options));
        }
        catch { /* best-effort */ }
    }
}
