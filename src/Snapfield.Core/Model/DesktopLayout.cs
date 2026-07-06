using Snapfield.Core.Geometry;

namespace Snapfield.Core.Model;

/// <summary>
/// The complete set of monitors across every machine, laid out on one shared
/// global physical plane. This is the single source of truth the mapper reads.
/// Immutable: rebuild it whenever a monitor is added, removed, or recalibrated.
/// </summary>
public sealed class DesktopLayout
{
    private readonly Dictionary<string, MonitorInfo> _byKey;
    private readonly Dictionary<string, List<MonitorInfo>> _byMachine;

    public DesktopLayout(IEnumerable<MonitorInfo> monitors)
    {
        // Tolerate duplicate keys (e.g. a peer echoing monitors we also have):
        // keep the first occurrence instead of crashing the whole session.
        var list = new List<MonitorInfo>();
        var byKey = new Dictionary<string, MonitorInfo>();
        var byMachine = new Dictionary<string, List<MonitorInfo>>();
        foreach (var m in monitors)
        {
            if (!byKey.TryAdd(m.Key, m)) continue;
            list.Add(m);
            if (!byMachine.TryGetValue(m.MachineId, out var perMachine))
                byMachine.Add(m.MachineId, perMachine = new List<MonitorInfo>());
            perMachine.Add(m);
        }
        Monitors = list;
        _byKey = byKey;
        _byMachine = byMachine;
    }

    public IReadOnlyList<MonitorInfo> Monitors { get; }

    public MonitorInfo? Find(string key) => _byKey.GetValueOrDefault(key);

    // Indexed at construction — this runs on the hook thread for every mouse
    // move, so it must not allocate.
    public IReadOnlyList<MonitorInfo> MonitorsOf(string machineId) =>
        _byMachine.TryGetValue(machineId, out var list) ? list : Array.Empty<MonitorInfo>();

    /// <summary>Bounding rectangle of the entire global plane, in millimetres.</summary>
    public PhysicalRect Bounds
    {
        get
        {
            if (Monitors.Count == 0) return default;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var m in Monitors)
            {
                minX = Math.Min(minX, m.PhysicalBounds.XMm);
                minY = Math.Min(minY, m.PhysicalBounds.YMm);
                maxX = Math.Max(maxX, m.PhysicalBounds.Right);
                maxY = Math.Max(maxY, m.PhysicalBounds.Bottom);
            }
            return new PhysicalRect(minX, minY, maxX - minX, maxY - minY);
        }
    }
}
