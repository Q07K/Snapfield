using Snapfield.Core.Geometry;
using Snapfield.Core.Input;
using Snapfield.Core.Model;
using Xunit;

namespace Snapfield.Core.Tests;

public class CursorRouterTests
{
    // Local machine "A": 27" 4K at global origin.
    private static MonitorInfo Local() => new()
    {
        MachineId = "A",
        DeviceId = "A-27",
        PixelBounds = new PixelRect(0, 0, 3840, 2160),
        PhysicalBounds = new PhysicalRect(0, 0, 597.6, 336.2),
    };

    // Remote machine "B": 24" 1080p to the RIGHT, vertically centred (top = 18.65 mm).
    private static MonitorInfo Remote() => new()
    {
        MachineId = "B",
        DeviceId = "B-24",
        PixelBounds = new PixelRect(0, 0, 1920, 1080),
        PhysicalBounds = new PhysicalRect(597.6, 18.65, 531.4, 298.9),
    };

    private static CursorRouter Router() =>
        new("A", new DesktopLayout(new[] { Local(), Remote() }));

    [Fact]
    public void PressingRightEdge_WithRemoteBeyond_HandsOffToRemote()
    {
        var r = Router();
        r.SeatLocal(1920, 1080); // centre of local

        // Cursor pinned at the local right edge, vertically centred.
        var result = r.OnLocalAbsolute(3839, 1080);

        Assert.Equal(RouteTransition.ToRemote, result.Transition);
        Assert.Equal("B", result.Owner!.MachineId);
        Assert.False(r.IsLocalActive);
    }

    [Fact]
    public void HandoffSeed_LandsAtCorrectPhysicalHeight_OnRemote()
    {
        var r = Router();
        r.SeatLocal(1920, 1080);

        // Leave local at vertical centre (physical y ~= 168.1 mm). On the remote,
        // whose span is 18.65..317.55 mm, that is fraction ~0.5 -> pixel Y ~= 540.
        var result = r.OnLocalAbsolute(3839, 1080);

        Assert.Equal(540, result.PixelY, 0);   // NOT 0 or 1080 — physical height preserved
        Assert.InRange(result.PixelX, 0, 10);  // seeded just inside remote's left edge
    }

    [Fact]
    public void MiddleOfScreen_DoesNotHandOff()
    {
        var r = Router();
        r.SeatLocal(1920, 1080);
        var result = r.OnLocalAbsolute(1920, 1080);
        Assert.Equal(RouteTransition.None, result.Transition);
        Assert.True(r.IsLocalActive);
    }

    [Fact]
    public void RightEdge_WithNoRemoteBeyond_DoesNotHandOff()
    {
        // Layout with only the local monitor: nothing across the right edge.
        var r = new CursorRouter("A", new DesktopLayout(new[] { Local() }));
        r.SeatLocal(1920, 1080);
        var result = r.OnLocalAbsolute(3839, 1080);
        Assert.Equal(RouteTransition.None, result.Transition);
        Assert.True(r.IsLocalActive);
    }

    [Fact]
    public void WhileRemote_MovingBackLeft_ReturnsToLocal_AtCorrectHeight()
    {
        var r = Router();
        r.SeatLocal(1920, 1080);
        r.OnLocalAbsolute(3839, 1080);          // -> remote, virtual near (597.6, 168.1)
        Assert.False(r.IsLocalActive);

        // Push left far enough to cross back over the seam into the local monitor.
        var result = r.OnDelta(-5.0, 0);

        Assert.Equal(RouteTransition.ToLocal, result.Transition);
        Assert.Equal("A", result.Owner!.MachineId);
        Assert.True(r.IsLocalActive);
        // Physical y ~168.1 mm on the local 4K -> pixel Y ~= 1080 (its centre).
        Assert.Equal(1080, result.PixelY, 0);
    }

    [Fact]
    public void WhileRemote_MovingWithinRemote_StaysRemote()
    {
        var r = Router();
        r.SeatLocal(1920, 1080);
        r.OnLocalAbsolute(3839, 1080);

        var result = r.OnDelta(50, 20); // deeper into the remote screen
        Assert.Equal(RouteTransition.None, result.Transition);
        Assert.Equal("B", result.Owner!.MachineId);
        Assert.False(r.IsLocalActive);
    }
}
