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
    public bool IsInternal { get; init; }
    /// <summary>DeviceKind as int (0 = unspecified → derive from IsInternal).</summary>
    public int Kind { get; init; }

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
        IsInternal = m.IsInternal,
        Kind = (int)m.Kind,
    };

    public MonitorInfo ToMonitorInfo() => new()
    {
        MachineId = MachineId,
        DeviceId = DeviceId,
        DisplayName = DisplayName,
        PixelBounds = new PixelRect(PixelLeft, PixelTop, PixelWidth, PixelHeight),
        PhysicalBounds = new PhysicalRect(PhysicalXMm, PhysicalYMm, PhysicalWidthMm, PhysicalHeightMm),
        DpiScale = DpiScale,
        IsInternal = IsInternal,
        Kind = (DeviceKind)Kind,
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

    /// <summary>Canonical per-user location of the global-plane layout.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snapfield", "layout.json");

    /// <summary>Raised after every successful <see cref="Save"/> — lets a running
    /// session re-route against the newly calibrated plane immediately.</summary>
    public static event Action? Saved;

    // The UI thread (calibration) and network reader threads (receiver applying
    // the controller's plane) both save — serialize the writes.
    private static readonly object WriteGate = new();

    /// <summary>Best-effort save: never throws (a disk hiccup must not crash the
    /// caller), never leaves a half-written file, raises <see cref="Saved"/> only
    /// when the file actually changed on disk.</summary>
    public static void Save(string path, DesktopLayout layout)
    {
        var states = layout.Monitors.Select(MonitorState.From).ToList();
        var json = JsonSerializer.Serialize(states, Options);
        try
        {
            lock (WriteGate)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch { return; }
        Saved?.Invoke();
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
    /// Merges freshly-detected LOCAL monitors with a saved layout: keeps each
    /// detected monitor's current pixel bounds + EDID size, restores the saved
    /// physical PLACEMENT for monitors the user previously calibrated, and keeps
    /// every saved monitor that belongs to OTHER machines (remote peers live on
    /// the same global plane but are never "detected" locally).
    /// </summary>
    public static DesktopLayout Merge(IEnumerable<MonitorInfo> detected, DesktopLayout? saved)
    {
        var local = detected.ToList();
        if (saved is null) return new DesktopLayout(local);

        var merged = local.Select(d =>
        {
            var prior = saved.Find(d.Key);
            if (prior is null) return d;
            // Restore saved placement (and any user-corrected physical size).
            return d with { PhysicalBounds = prior.PhysicalBounds };
        }).ToList();

        var localMachines = local.Select(m => m.MachineId).ToHashSet();
        merged.AddRange(saved.Monitors.Where(m => !localMachines.Contains(m.MachineId)));

        return new DesktopLayout(merged);
    }
}
