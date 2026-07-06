using System.Text.Json;

namespace Snapfield.Core.Persistence;

/// <summary>A named snapshot of the physical plane (집 / 사무실 / …), so a laptop
/// that moves between desks doesn't have to be re-arranged every time.</summary>
public sealed record LayoutPreset
{
    public string Name { get; init; } = "";
    public List<MonitorState> Monitors { get; init; } = new();
}

public static class PresetStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snapfield", "presets.json");

    public static List<LayoutPreset> Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
                return JsonSerializer.Deserialize<List<LayoutPreset>>(File.ReadAllText(DefaultPath), Options) ?? new();
        }
        catch { /* corrupted -> start empty */ }
        return new();
    }

    /// <summary>Adds or replaces the preset with the same name.</summary>
    public static void Upsert(LayoutPreset preset)
    {
        var all = Load();
        all.RemoveAll(p => p.Name == preset.Name);
        all.Add(preset);
        Save(all);
    }

    public static void Delete(string name)
    {
        var all = Load();
        if (all.RemoveAll(p => p.Name == name) > 0) Save(all);
    }

    private static void Save(List<LayoutPreset> presets)
    {
        try
        {
            var dir = Path.GetDirectoryName(DefaultPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(DefaultPath, JsonSerializer.Serialize(presets, Options));
        }
        catch { /* best-effort */ }
    }
}
