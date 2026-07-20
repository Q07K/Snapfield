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
    public void CapturedTraversal_CrossesRemoteToRemote_AcrossACalibrationGap()
    {
        // The two-phones-side-by-side layout: laptop, then phone B, then phone C
        // with a small calibration gap between the phones. Reaching C means a
        // remote→remote hop while captured — OnDelta must bridge the gap the
        // same way the local-edge handoff does.
        var local = Local(); // 0..597.6 mm
        var phoneB = new MonitorInfo
        {
            MachineId = "B", DeviceId = "B-phone",
            PixelBounds = new PixelRect(0, 0, 1080, 2400),
            PhysicalBounds = new PhysicalRect(597.6, 0, 64, 142),
        };
        var phoneC = new MonitorInfo
        {
            MachineId = "C", DeviceId = "C-phone",
            PixelBounds = new PixelRect(0, 0, 1440, 3200),
            PhysicalBounds = new PhysicalRect(597.6 + 64 + 8, 0, 71, 158), // 8 mm of daylight
        };
        var r = new CursorRouter("A", new DesktopLayout(new[] { local, phoneB, phoneC }));
        r.SeatLocal(1920, 1080);
        r.OnLocalAbsolute(3839, 100); // hand off onto phone B

        // Slow push rightwards: small deltas must NOT stick at B's right edge.
        RouteResult last = default;
        for (var i = 0; i < 40; i++) last = r.OnDelta(2, 0);

        Assert.Equal("C", last.Owner!.MachineId);
    }

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
    public void BottomEdge_RemoteDiagonallyBelow_NoSpanOverlap_DoesNotHandOff()
    {
        // The field layout: company laptop top-left, wide monitor top-right,
        // GYU laptop below the monitor. GYU's top edge is within the seam gap
        // below the company laptop's bottom, but sits ~300 mm to the RIGHT with
        // zero horizontal overlap — pushing down must NOT jump diagonally.
        var laptop = Local(); // machine A: x 0..597.6, y 0..336.2
        var gyu = new MonitorInfo
        {
            MachineId = "B", DeviceId = "B-gyu",
            PixelBounds = new PixelRect(0, 0, 2880, 1800),
            PhysicalBounds = new PhysicalRect(900, 340, 340, 213), // 302 mm right of A, 4 mm below
        };
        var r = new CursorRouter("A", new DesktopLayout(new[] { laptop, gyu }));
        r.SeatLocal(1920, 1080);

        var result = r.OnLocalAbsolute(1920, 2159); // pinned at A's bottom edge, centred

        Assert.Equal(RouteTransition.None, result.Transition);
        Assert.True(r.IsLocalActive);
    }

    [Fact]
    public void BottomEdge_RemoteBelow_SlightlyOffBand_StillCrosses()
    {
        // The lateral cap must not kill the flick tolerance: a remote whose span
        // starts 40 mm right of the cursor's exit point (within SeamGapMm) crosses.
        var laptop = Local();
        var below = new MonitorInfo
        {
            MachineId = "B", DeviceId = "B-below",
            PixelBounds = new PixelRect(0, 0, 1920, 1080),
            PhysicalBounds = new PhysicalRect(637.6, 340, 531.4, 298.9), // starts 40 mm past A's right
        };
        var r = new CursorRouter("A", new DesktopLayout(new[] { laptop, below }));
        r.SeatLocal(1920, 1080);

        var result = r.OnLocalAbsolute(3839, 2159); // A's bottom-right corner

        Assert.Equal(RouteTransition.ToRemote, result.Transition);
        Assert.Equal("B", result.Owner!.MachineId);
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
    public void SlowReturn_LandsOffTheHandoffRow_AndDoesNotRecapture()
    {
        // Creeping back across the seam used to land the cursor EXACTLY on the
        // edge row that triggers handoff (x = Right-1) — one jitter event later
        // it re-captured, and the cursor ping-ponged between the seam and the
        // parked centre ("infinite loop on the same screen").
        var r = Router();
        r.SeatLocal(1920, 1080);
        r.OnLocalAbsolute(3839, 1080); // -> remote

        RouteResult back = default;
        for (var i = 0; i < 10 && back.Transition != RouteTransition.ToLocal; i++)
            back = r.OnDelta(-0.4, 0); // sub-millimetre creep home

        Assert.Equal(RouteTransition.ToLocal, back.Transition);
        Assert.True(back.PixelX <= 3836, $"landed on/next to the trigger row: {back.PixelX}");

        // Feeding the landing position straight back must not hand off again.
        var again = r.OnLocalAbsolute(back.PixelInt.X, back.PixelInt.Y);
        Assert.Equal(RouteTransition.None, again.Transition);
        Assert.True(r.IsLocalActive);
    }

    [Fact]
    public void ReturnFromAbove_LandsBelowTheTopRow()
    {
        // Vertical stack: remote ABOVE the local monitor. Coming down, the
        // crossing point is a hair inside the top edge, which rounded to row 0 —
        // the top-edge trigger — every single time: the pointer "never came
        // down", bouncing back up on the next event.
        var above = new MonitorInfo
        {
            MachineId = "B", DeviceId = "B-above",
            PixelBounds = new PixelRect(0, 0, 1920, 1080),
            PhysicalBounds = new PhysicalRect(0, -298.9, 531.4, 298.9), // stacked on top of A
        };
        var r = new CursorRouter("A", new DesktopLayout(new[] { Local(), above }));
        r.SeatLocal(1920, 1080);
        r.OnLocalAbsolute(960, 0); // hand off upward
        Assert.False(r.IsLocalActive);

        RouteResult back = default;
        for (var i = 0; i < 10 && back.Transition != RouteTransition.ToLocal; i++)
            back = r.OnDelta(0, 0.4); // creep back down

        Assert.Equal(RouteTransition.ToLocal, back.Transition);
        Assert.True(back.PixelY >= 3, $"landed on the top trigger row: {back.PixelY}");

        var again = r.OnLocalAbsolute(back.PixelInt.X, back.PixelInt.Y);
        Assert.Equal(RouteTransition.None, again.Transition);
        Assert.True(r.IsLocalActive);
    }

    [Fact]
    public void BottomEdge_LocalNeighbourAcrossAGap_BlocksTheRemote()
    {
        // Local A on top, local A2 below it with a few mm of calibration
        // daylight, and a remote within the seam gap off to the right. Pushing
        // down where A2 spans must stay local — the remote-only edge scan used
        // to teleport control to R because A2 was never a candidate.
        var a = Local();
        var a2 = new MonitorInfo
        {
            MachineId = "A", DeviceId = "A-lower",
            PixelBounds = new PixelRect(0, 2160, 3840, 2160),
            PhysicalBounds = new PhysicalRect(0, 341, 597.6, 336.2), // ~5 mm gap below A
        };
        var remote = new MonitorInfo
        {
            MachineId = "B", DeviceId = "B-right",
            PixelBounds = new PixelRect(0, 0, 1920, 1080),
            PhysicalBounds = new PhysicalRect(650, 350, 531.4, 298.9), // within SeamGapMm sideways
        };
        var r = new CursorRouter("A", new DesktopLayout(new[] { a, a2, remote }));
        r.SeatLocal(1920, 1080);

        var result = r.OnLocalAbsolute(3800, 2159); // A's bottom edge, near the right

        Assert.Equal(RouteTransition.None, result.Transition);
        Assert.True(r.IsLocalActive);
    }

    [Fact]
    public void HugeOvershoot_EntryPixel_StaysInsideTheRemote()
    {
        // A flick overshooting past the remote's far edge used to clamp the
        // seed onto the rect's INCLUSIVE bottom edge, whose pixel mapping is one
        // row past the last (y = Height) — the receiver warped outside its
        // monitor and the pointer landed on the wrong screen or vanished.
        var r = Router();
        r.SeatLocal(1920, 1080);

        var result = r.OnLocalAbsolute(4482, 4000); // wildly past bottom-right

        Assert.Equal(RouteTransition.ToRemote, result.Transition);
        Assert.InRange(result.PixelInt.X, 0, 1919);
        Assert.InRange(result.PixelInt.Y, 0, 1079);
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
