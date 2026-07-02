using System.Text.Json;

namespace Snapfield.Core.Persistence;

/// <summary>App-level settings, persisted next to the layout file.</summary>
public sealed record AppSettings
{
    /// <summary>"Controller", "Receiver", or null when no session has run yet.</summary>
    public string? LastRole { get; init; }
    public string LastHost { get; init; } = "";
    public int LastPort { get; init; } = 45654;

    /// <summary>Re-start the last session automatically when the app launches.</summary>
    public bool RestoreOnLaunch { get; init; } = true;
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
