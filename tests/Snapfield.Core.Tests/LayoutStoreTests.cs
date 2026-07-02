using Snapfield.Core.Geometry;
using Snapfield.Core.Model;
using Snapfield.Core.Persistence;
using Xunit;

namespace Snapfield.Core.Tests;

public class LayoutStoreTests
{
    private static MonitorInfo Monitor(string device, double xMm, double yMm) => new()
    {
        MachineId = "PC1",
        DeviceId = device,
        DisplayName = device + " display",
        PixelBounds = new PixelRect(0, 0, 1920, 1080),
        PhysicalBounds = new PhysicalRect(xMm, yMm, 520, 290),
        DpiScale = 1.25,
    };

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"snapfield_{Guid.NewGuid():N}.json");
        try
        {
            var layout = new DesktopLayout(new[] { Monitor("MON-A", 0, 0), Monitor("MON-B", 520, 30) });
            LayoutStore.Save(path, layout);

            var loaded = LayoutStore.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.Monitors.Count);

            var b = loaded.Find("PC1/MON-B");
            Assert.NotNull(b);
            Assert.Equal(520, b!.PhysicalBounds.XMm, 3);
            Assert.Equal(30, b.PhysicalBounds.YMm, 3);
            Assert.Equal(1.25, b.DpiScale, 3);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nope_{Guid.NewGuid():N}.json");
        Assert.Null(LayoutStore.Load(path));
    }

    [Fact]
    public void Merge_RestoresSavedPlacement_ForKnownMonitor_AndSeedsNew()
    {
        // Saved layout: MON-A was calibrated to a custom position.
        var saved = new DesktopLayout(new[] { Monitor("MON-A", 111, 222) });

        // Freshly detected: MON-A (provisional seed at 0,0) + a brand-new MON-C.
        var detected = new[]
        {
            Monitor("MON-A", 0, 0),
            Monitor("MON-C", 999, 0),
        };

        var merged = LayoutStore.Merge(detected, saved);

        // Known monitor keeps its saved placement...
        var a = merged.Find("PC1/MON-A")!;
        Assert.Equal(111, a.PhysicalBounds.XMm, 3);
        Assert.Equal(222, a.PhysicalBounds.YMm, 3);

        // ...new monitor keeps its fresh seed.
        var c = merged.Find("PC1/MON-C")!;
        Assert.Equal(999, c.PhysicalBounds.XMm, 3);
    }

    [Fact]
    public void Merge_WithNoSavedLayout_ReturnsDetectedAsIs()
    {
        var detected = new[] { Monitor("MON-A", 0, 0) };
        var merged = LayoutStore.Merge(detected, null);
        Assert.Single(merged.Monitors);
        Assert.Equal(0, merged.Find("PC1/MON-A")!.PhysicalBounds.XMm, 3);
    }
}
