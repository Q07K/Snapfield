using Snapfield.Core.Input;
using Xunit;

namespace Snapfield.Core.Tests;

public class SeamScannerTests
{
    [Fact]
    public void TallerRemote_SplitsEdgeIntoDirectBandAndOffBands()
    {
        // The real ULTRON case: 15.6" laptop next to a taller 24" remote whose
        // panel overhangs above and below the laptop's edge.
        var laptop = new SeamRect("GYU", 0, 56, 344, 215);        // y 56..271
        var ultron = new SeamRect("K-GYU", 344, 0, 531, 299);      // y 0..299

        var segs = SeamScanner.Scan(new[] { laptop, ultron });

        var direct = Assert.Single(segs, s => s.Direct);
        Assert.True(direct.Vertical);
        Assert.Equal(344, direct.SeamMm, 3);
        Assert.Equal(56, direct.StartMm, 3);
        Assert.Equal(271, direct.EndMm, 3);

        // Off-band: the remote's overhang above (0..56) and below (271..299).
        var offs = segs.Where(s => !s.Direct).OrderBy(s => s.StartMm).ToList();
        Assert.Equal(2, offs.Count);
        Assert.Equal(0, offs[0].StartMm, 3);
        Assert.Equal(56, offs[0].EndMm, 3);
        Assert.Equal(271, offs[1].StartMm, 3);
        Assert.Equal(299, offs[1].EndMm, 3);
    }

    [Fact]
    public void PerfectlyAlignedPair_IsOneDirectSegmentOnly()
    {
        var a = new SeamRect("A", 0, 0, 600, 336);
        var b = new SeamRect("B", 600, 0, 600, 336);

        var segs = SeamScanner.Scan(new[] { a, b });

        var s = Assert.Single(segs);
        Assert.True(s.Direct);
        Assert.Equal(0, s.StartMm, 3);
        Assert.Equal(336, s.EndMm, 3);
    }

    [Fact]
    public void CalibrationGap_WithinTolerance_SeamSitsMidGap()
    {
        var a = new SeamRect("A", 0, 0, 600, 336);
        var b = new SeamRect("B", 630, 0, 600, 336); // 30 mm of daylight

        var segs = SeamScanner.Scan(new[] { a, b });

        var s = Assert.Single(segs, x => x.Direct);
        Assert.Equal(615, s.SeamMm, 3); // midpoint of the gap
    }

    [Fact]
    public void GapBeyondTolerance_YieldsNothing()
    {
        var a = new SeamRect("A", 0, 0, 600, 336);
        var b = new SeamRect("B", 600 + SeamScanner.SeamGapMm + 1, 0, 600, 336);

        Assert.Empty(SeamScanner.Scan(new[] { a, b }));
    }

    [Fact]
    public void SameMachinePair_IsIgnored()
    {
        var a = new SeamRect("A", 0, 0, 600, 336);
        var b = new SeamRect("A", 600, 0, 600, 336);

        Assert.Empty(SeamScanner.Scan(new[] { a, b }));
    }

    [Fact]
    public void RemoteAbove_ProducesHorizontalSeam()
    {
        // Remote sits ABOVE the laptop across a 10 mm gap (the verified v0.6 setup).
        var remote = new SeamRect("K-GYU", 0, 0, 531, 299);
        var laptop = new SeamRect("GYU", 100, 309, 344, 215);

        var segs = SeamScanner.Scan(new[] { laptop, remote });

        var direct = Assert.Single(segs, s => s.Direct);
        Assert.False(direct.Vertical);
        Assert.Equal(304, direct.SeamMm, 3);   // midpoint of the 10 mm gap
        Assert.Equal(100, direct.StartMm, 3);  // overlap in X: 100..444
        Assert.Equal(444, direct.EndMm, 3);
    }

    [Fact]
    public void OffBandFromOnePair_YieldsToDirectBandFromAnother()
    {
        // The field bug: local laptop sits under a wide remote monitor (direct band
        // across its whole top edge), while a second remote's bottom edge lies on
        // almost the same seam line further left. That pair has no X overlap with
        // the laptop, so pairwise it would spray ✕ over the very stretch where the
        // arrows are — but globally the cursor DOES cross there.
        var laptop = new SeamRect("GYU", 300, 300, 340, 220);
        var wide = new SeamRect("VICS", 0, 0, 800, 300);           // directly above, seam y=300
        var side = new SeamRect("VICS", -400, 90, 250, 208);       // bottom edge y=298, far left

        var segs = SeamScanner.Scan(new[] { laptop, wide, side });

        // The direct band over the laptop's top edge survives untouched…
        Assert.Contains(segs, s => s.Direct && !s.Vertical &&
            Math.Abs(s.StartMm - 300) < 1 && Math.Abs(s.EndMm - 640) < 1);
        // …and no off-band ✕ overlaps it, from any pair.
        Assert.DoesNotContain(segs, s => !s.Direct && !s.Vertical && s.EndMm > 301 && s.StartMm < 639);
    }

    [Fact]
    public void DisjointHeights_WithinGap_AreAllOffBand()
    {
        // Adjacent in X but no shared height: the router still hands off with a
        // clamped entry, so both edges show as off-band, none direct.
        var a = new SeamRect("A", 0, 0, 600, 200);
        var b = new SeamRect("B", 600, 250, 600, 200);

        var segs = SeamScanner.Scan(new[] { a, b });

        Assert.DoesNotContain(segs, s => s.Direct);
        Assert.Equal(2, segs.Count);
    }
}
