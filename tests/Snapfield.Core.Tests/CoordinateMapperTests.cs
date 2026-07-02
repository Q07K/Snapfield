using Snapfield.Core.Geometry;
using Snapfield.Core.Model;
using Snapfield.Core.Transforms;
using Xunit;

namespace Snapfield.Core.Tests;

public class CoordinateMapperTests
{
    // 27" 4K on machine A, anchored at global origin.
    //   3840x2160 px, physical 597.6 x 336.2 mm, placed at (0,0).
    private static MonitorInfo Mon27_4K() => new()
    {
        MachineId = "A",
        DeviceId = "A-27",
        DisplayName = "A 27\" 4K",
        PixelBounds = new PixelRect(0, 0, 3840, 2160),
        PhysicalBounds = new PhysicalRect(0, 0, 597.6, 336.2),
    };

    // 24" 1080p on machine B, placed to the RIGHT of the 27", vertically centred.
    //   1920x1080 px, physical 531.4 x 298.9 mm.
    //   Centre-aligned: top = (336.2 - 298.9) / 2 = 18.65 mm; left = 597.6 mm.
    // Note machine B numbers its own desktop from 0 — its pixel origin is unrelated to A.
    private static MonitorInfo Mon24_1080_Right() => new()
    {
        MachineId = "B",
        DeviceId = "B-24",
        DisplayName = "B 24\" 1080p",
        PixelBounds = new PixelRect(0, 0, 1920, 1080),
        PhysicalBounds = new PhysicalRect(597.6, 18.65, 531.4, 298.9),
    };

    [Fact]
    public void PixelToPhysical_RoundTrips_WithinMonitor()
    {
        var m = Mon27_4K();
        var mapper = new CoordinateMapper(new DesktopLayout(new[] { m }));

        var phys = mapper.PixelToPhysical(m, 1920, 1080); // dead centre
        Assert.Equal(298.8, phys.XMm, 3);
        Assert.Equal(168.1, phys.YMm, 3);

        var (px, py) = mapper.PhysicalToPixel(m, phys);
        Assert.Equal(1920, px, 6);
        Assert.Equal(1080, py, 6);
    }

    [Fact]
    public void CrossingMachines_LandsAtSamePhysicalHeight()
    {
        // This is the MWB failure mode: leaving the 27" at mid-height must arrive
        // at mid-height on the 24" — NOT at a pixel-matched Y that would "jump".
        var a = Mon27_4K();
        var b = Mon24_1080_Right();
        var mapper = new CoordinateMapper(new DesktopLayout(new[] { a, b }));

        // Cursor at the right edge of A, vertically centred (physical y = 168.1 mm).
        var exit = mapper.PixelToPhysical(a, 3839, 1080);

        // Nudge just across the seam into B's physical territory.
        var justInsideB = exit with { XMm = 597.6 + 0.5 };

        var loc = mapper.Resolve(justInsideB);
        Assert.NotNull(loc);
        Assert.Equal("B", loc!.Value.MachineId);

        // Physical mid-height on B maps to B's vertical pixel centre (~540), not 0 or 1080.
        Assert.Equal(540, loc.Value.PixelY, 0);
    }

    [Fact]
    public void NaiveMatrixMapping_WouldMisplace_ProvingWhyPhysicalMatters()
    {
        // MWB-style: it maps the exit fraction of the source screen directly onto
        // the destination. Here both screens are full-height in their own space,
        // so a fraction-based hand-off keeps mid → mid by luck. The real breakage
        // shows when the screens are NOT height-aligned. Model a 24" shifted DOWN
        // so its top sits level with the 27" top (top = 0), bottom at 298.9 mm.
        var a = Mon27_4K();
        var bTopAligned = Mon24_1080_Right() with
        {
            PhysicalBounds = new PhysicalRect(597.6, 0, 531.4, 298.9),
        };
        var mapper = new CoordinateMapper(new DesktopLayout(new[] { a, bTopAligned }));

        // Leave A at its vertical centre: physical y = 168.1 mm.
        var exit = mapper.PixelToPhysical(a, 3839, 1080);
        var justInsideB = exit with { XMm = 597.6 + 0.5 };
        var loc = mapper.Resolve(justInsideB)!.Value;

        // Physically 168.1 mm down a screen that spans 0..298.9 mm -> fraction 0.562
        // -> pixel Y ~= 607. A pixel-fraction (0.5 * 1080 = 540) hand-off would be WRONG
        // because the screens no longer share a vertical origin.
        Assert.Equal(607, loc.PixelY, 0);
        Assert.NotEqual(540, (int)System.Math.Round(loc.PixelY));
    }

    [Fact]
    public void Resolve_InGap_FallsBackToNearestMonitor()
    {
        var a = Mon27_4K();
        var b = Mon24_1080_Right() with
        {
            // Leave a 40 mm physical gap between the two screens.
            PhysicalBounds = new PhysicalRect(597.6 + 40, 18.65, 531.4, 298.9),
        };
        var mapper = new CoordinateMapper(new DesktopLayout(new[] { a, b }));

        // A point sitting in the dead zone between the screens.
        var inGap = new PhysicalPoint(597.6 + 20, 168.1);
        var loc = mapper.Resolve(inGap);

        Assert.NotNull(loc);
        // Nearest edge is A's right edge -> clamps onto A at its far-right column.
        Assert.Equal("A", loc!.Value.MachineId);
        Assert.Equal(3840, loc.Value.PixelX, 0);
    }

    [Fact]
    public void HitTest_PicksContainingMonitor()
    {
        var a = Mon27_4K();
        var b = Mon24_1080_Right();
        var mapper = new CoordinateMapper(new DesktopLayout(new[] { a, b }));

        Assert.Equal("A-27", mapper.HitTest(new PhysicalPoint(100, 100))?.DeviceId);
        Assert.Equal("B-24", mapper.HitTest(new PhysicalPoint(800, 150))?.DeviceId);
        Assert.Null(mapper.HitTest(new PhysicalPoint(5000, 5000)));
    }
}
