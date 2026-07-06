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
    public void JumpToMachine_SeatsVirtualCursorOnRemoteCentre_AndBack()
    {
        var r = Router();
        r.SeatLocal(1920, 1080); // centre of local

        var jump = r.JumpToMachine("B");
        Assert.Equal(RouteTransition.ToRemote, jump.Transition);
        Assert.Equal("B", jump.Owner!.MachineId);
        Assert.Equal(597.6 + 531.4 / 2, r.Virtual.XMm, 1); // centre of B

        var back = r.JumpToMachine("A");
        Assert.Equal(RouteTransition.ToLocal, back.Transition);
        Assert.Equal("A", back.Owner!.MachineId);
    }

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
    public void FastFlick_OffRemoteBand_StillCrosses()
    {
        // Remote spans physical Y 18.65..317.55. A fast flick pins the cursor at the
        // top-right corner (physical Y ~1.5 mm) — above the remote's band. The old
        // point-probe missed here ("stuck until you slow down"); the edge scan now
        // crosses and clamps entry to the remote's top.
        var r = Router();
        r.SeatLocal(1920, 1080);

        var result = r.OnLocalAbsolute(3839, 8); // top-right edge

        Assert.Equal(RouteTransition.ToRemote, result.Transition);
        Assert.Equal("B", result.Owner!.MachineId);
        Assert.True(result.PixelY >= 0 && result.PixelY < 40); // clamped near the remote's top
    }

    [Fact]
    public void FastFlick_OvershootBeyondEdge_HandsOffImmediately()
    {
        // WH_MOUSE_LL delivers the PROPOSED position, which lands past the
        // desktop edge on a fast move (measured: e.g. x=3214 on a 2880-wide
        // screen). Such an event must hand off, not be discarded — discarding
        // it made fast crossings stall at the seam.
        var r = Router();
        r.SeatLocal(1920, 1080);

        var result = r.OnLocalAbsolute(4200, 1080); // 361px past the right edge

        Assert.Equal(RouteTransition.ToRemote, result.Transition);
        Assert.Equal("B", result.Owner!.MachineId);
        Assert.False(r.IsLocalActive);
    }

    [Fact]
    public void FastFlick_Overshoot_CarriesMomentumIntoRemote()
    {
        // +643px past the edge ≈ 100mm on the local panel (3840px / 597.6mm).
        // The remote entry point should sit ~100mm past its left edge
        // (531.4mm / 1920px → ~361px), not at the seam.
        var r = Router();
        r.SeatLocal(1920, 1080);

        var result = r.OnLocalAbsolute(4482, 1080);

        Assert.Equal(RouteTransition.ToRemote, result.Transition);
        Assert.InRange(result.PixelX, 300, 430);
        Assert.Equal(540, result.PixelY, 0); // physical height still preserved
    }

    [Fact]
    public void Overshoot_WithNoRemoteBeyond_StaysLocal()
    {
        var r = new CursorRouter("A", new DesktopLayout(new[] { Local() }));
        r.SeatLocal(1920, 1080);

        var result = r.OnLocalAbsolute(4200, 1080);

        Assert.Equal(RouteTransition.None, result.Transition);
        Assert.True(r.IsLocalActive);
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
