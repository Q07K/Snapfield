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

    public DesktopLayout(IEnumerable<MonitorInfo> monitors)
    {
        Monitors = monitors.ToList();
        _byKey = Monitors.ToDictionary(m => m.Key);
    }

    public IReadOnlyList<MonitorInfo> Monitors { get; }

    public MonitorInfo? Find(string key) => _byKey.GetValueOrDefault(key);

    public IEnumerable<MonitorInfo> MonitorsOf(string machineId) =>
        Monitors.Where(m => m.MachineId == machineId);

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
