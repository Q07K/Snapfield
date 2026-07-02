using Snapfield.Core.Geometry;
using Snapfield.Core.Input;
using Snapfield.Core.Model;
using Snapfield.Core.Persistence;
using Xunit;

namespace Snapfield.Core.Tests;

public class CalibratedLayoutTests
{
    private static MonitorInfo Local() => new()
    {
        MachineId = "A",
        DeviceId = "A-27",
        PixelBounds = new PixelRect(0, 0, 3840, 2160),
        PhysicalBounds = new PhysicalRect(0, 0, 597.6, 336.2),
    };

    private static MonitorInfo Remote(double xMm = 0, double yMm = 0) => new()
    {
        MachineId = "B",
        DeviceId = "B-24",
        PixelBounds = new PixelRect(0, 0, 1920, 1080),
        PhysicalBounds = new PhysicalRect(xMm, yMm, 531.4, 298.9),
    };

    // ── CursorRouter gap probe ───────────────────────────────────────────────

    [Fact]
    public void Handoff_BridgesSmallCalibrationGap()
    {
        // Remote sits 20 mm right of the local edge (user left a gap when dragging).
        var a = Local();
        var b = Remote(a.PhysicalBounds.Right + 20, 18.65);
        var r = new CursorRouter("A", new DesktopLayout(new[] { a, b }));
        r.SeatLocal(1920, 1080);

        var result = r.OnLocalAbsolute(3839, 1080);

        Assert.Equal(RouteTransition.ToRemote, result.Transition);
        Assert.Equal("B", result.Owner!.MachineId);
    }

    [Fact]
    public void Handoff_DoesNotBridgeHugeGap()
    {
        // Far beyond both probes: no handoff.
        var a = Local();
        var b = Remote(a.PhysicalBounds.Right + 200, 18.65);
        var r = new CursorRouter("A", new DesktopLayout(new[] { a, b }));
        r.SeatLocal(1920, 1080);

        var result = r.OnLocalAbsolute(3839, 1080);
        Assert.Equal(RouteTransition.None, result.Transition);
    }

    // ── PhysicalLayoutBuilder.Calibrated ─────────────────────────────────────

    [Fact]
    public void Calibrated_UsesSavedPlacement_ForBothSides()
    {
        var local = Local();
        var remote = Remote();

        // User calibrated: remote is ABOVE the local monitor, not to the right.
        var saved = new DesktopLayout(new[]
        {
            local with { PhysicalBounds = new PhysicalRect(0, 0, 597.6, 336.2) },
            remote with { PhysicalBounds = new PhysicalRect(30, -298.9, 531.4, 298.9) },
        });

        var combined = PhysicalLayoutBuilder.Calibrated(new[] { local }, new[] { remote }, saved);
        var layout = new DesktopLayout(combined);

        var b = layout.Find("B/B-24")!;
        Assert.Equal(-298.9, b.PhysicalBounds.YMm, 3);  // saved placement, not auto-right
        Assert.Equal(30, b.PhysicalBounds.XMm, 3);

        // And routing honours it: pushing the TOP edge of local crosses to B.
        var r = new CursorRouter("A", layout);
        r.SeatLocal(1920, 1080);
        var result = r.OnLocalAbsolute(1920, 0);
        Assert.Equal(RouteTransition.ToRemote, result.Transition);
        Assert.Equal("B", result.Owner!.MachineId);
    }

    [Fact]
    public void Calibrated_FallsBackToAppendRight_WhenRemoteUnplaced()
    {
        var local = Local();
        var remote = Remote();

        var combined = PhysicalLayoutBuilder.Calibrated(new[] { local }, new[] { remote }, saved: null);
        var layout = new DesktopLayout(combined);

        var b = layout.Find("B/B-24")!;
        var a = layout.Find("A/A-27")!;
        Assert.Equal(a.PhysicalBounds.Right, b.PhysicalBounds.XMm, 3); // glued to the right
    }

    // ── LayoutStore.Merge keeps foreign machines ─────────────────────────────

    [Fact]
    public void Merge_KeepsRemoteMachineMonitors_FromSavedLayout()
    {
        var localDetected = new[] { Local() };
        var saved = new DesktopLayout(new[] { Local(), Remote(700, 10) });

        var merged = LayoutStore.Merge(localDetected, saved);

        Assert.Equal(2, merged.Monitors.Count);
        Assert.NotNull(merged.Find("B/B-24"));
        Assert.Equal(700, merged.Find("B/B-24")!.PhysicalBounds.XMm, 3);
    }
}
