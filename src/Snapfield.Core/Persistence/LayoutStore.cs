using System.Text.Json;
using System.Text.Json.Serialization;
using Snapfield.Core.Geometry;
using Snapfield.Core.Model;

namespace Snapfield.Core.Persistence;

/// <summary>Flat, serialization-friendly snapshot of one monitor's calibrated state.</summary>
public sealed record MonitorState
{
    public string MachineId { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int PixelLeft { get; init; }
    public int PixelTop { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public double PhysicalXMm { get; init; }
    public double PhysicalYMm { get; init; }
    public double PhysicalWidthMm { get; init; }
    public double PhysicalHeightMm { get; init; }
    public double DpiScale { get; init; } = 1.0;

    public string Key => $"{MachineId}/{DeviceId}";

    public static MonitorState From(MonitorInfo m) => new()
    {
        MachineId = m.MachineId,
        DeviceId = m.DeviceId,
        DisplayName = m.DisplayName,
        PixelLeft = m.PixelBounds.Left,
        PixelTop = m.PixelBounds.Top,
        PixelWidth = m.PixelBounds.Width,
        PixelHeight = m.PixelBounds.Height,
        PhysicalXMm = m.PhysicalBounds.XMm,
        PhysicalYMm = m.PhysicalBounds.YMm,
        PhysicalWidthMm = m.PhysicalBounds.WidthMm,
        PhysicalHeightMm = m.PhysicalBounds.HeightMm,
        DpiScale = m.DpiScale,
    };

    public MonitorInfo ToMonitorInfo() => new()
    {
        MachineId = MachineId,
        DeviceId = DeviceId,
        DisplayName = DisplayName,
        PixelBounds = new PixelRect(PixelLeft, PixelTop, PixelWidth, PixelHeight),
        PhysicalBounds = new PhysicalRect(PhysicalXMm, PhysicalYMm, PhysicalWidthMm, PhysicalHeightMm),
        DpiScale = DpiScale,
    };
}

/// <summary>Persists and reloads a calibrated <see cref="DesktopLayout"/> as JSON.</summary>
public static class LayoutStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static void Save(string path, DesktopLayout layout)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var states = layout.Monitors.Select(MonitorState.From).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(states, Options));
    }

    /// <summary>Loads a saved layout, or null if the file is missing/unreadable.</summary>
    public static DesktopLayout? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var states = JsonSerializer.Deserialize<List<MonitorState>>(File.ReadAllText(path), Options);
            if (states is null || states.Count == 0) return null;
            return new DesktopLayout(states.Select(s => s.ToMonitorInfo()));
        }
        catch { return null; }
    }

    /// <summary>
    /// Merges freshly-detected monitors with a saved layout: keeps each detected
    /// monitor's current pixel bounds + EDID size, but restores the saved physical
    /// PLACEMENT (position) for monitors the user previously calibrated. New
    /// monitors keep their provisional seed position.
    /// </summary>
    public static DesktopLayout Merge(IEnumerable<MonitorInfo> detected, DesktopLayout? saved)
    {
        if (saved is null) return new DesktopLayout(detected);
        var merged = detected.Select(d =>
        {
            var prior = saved.Find(d.Key);
            if (prior is null) return d;
            // Restore saved placement (and any user-corrected physical size).
            return d with { PhysicalBounds = prior.PhysicalBounds };
        });
        return new DesktopLayout(merged);
    }
}
