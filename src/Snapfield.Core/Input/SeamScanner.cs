namespace Snapfield.Core.Input;

/// <summary>A rectangle on the physical plane, tagged with its owning machine.</summary>
public readonly record struct SeamRect(string MachineId, double XMm, double YMm, double WidthMm, double HeightMm);

/// <summary>
/// One stretch of a seam between two monitors of DIFFERENT machines.
/// Direct = the two edges face each other here, so the cursor crosses at the
/// same height, like a native Windows seam. Non-direct = the pair is adjacent
/// but only one side has an edge at this height — since v0.9.1 the router still
/// hands off on a push there, but the entry point is clamped to the remote's
/// nearest band, so the cursor lands displaced.
/// </summary>
public readonly record struct SeamSegment(bool Vertical, double SeamMm, double StartMm, double EndMm, bool Direct);

/// <summary>
/// Computes where the cursor can cross between machines on the physical plane —
/// the calibration canvas draws the result (flow arrows on direct stretches,
/// blocked marks on off-band stretches). Pure and unit-testable; mirrors the
/// adjacency rule of <see cref="CursorRouter"/> (same seam-gap tolerance).
/// </summary>
public static class SeamScanner
{
    /// <summary>Keep in sync with <see cref="CursorRouter.SeamGapMm"/>.</summary>
    public const double SeamGapMm = 80.0;

    /// <summary>Ignore off-band slivers shorter than this (mm).</summary>
    private const double MinOffBandMm = 1.0;

    public static List<SeamSegment> Scan(IReadOnlyList<SeamRect> rects)
    {
        var direct = new List<SeamSegment>();
        var offBand = new List<SeamSegment>();
        for (var i = 0; i < rects.Count; i++)
            for (var j = 0; j < rects.Count; j++)
            {
                if (i == j) continue;
                var a = rects[i];
                var b = rects[j];
                if (a.MachineId == b.MachineId) continue; // Windows owns same-machine seams

                // Both orientations can qualify at once (corner-diagonal pairs) —
                // draw only the dominant one, else every corner sprays markers
                // down two whole edges.
                var v = Candidate(vertical: true,
                    edgeA: a.XMm + a.WidthMm, edgeB: b.XMm,
                    aLo: a.YMm, aHi: a.YMm + a.HeightMm,
                    bLo: b.YMm, bHi: b.YMm + b.HeightMm);
                var h = Candidate(vertical: false,
                    edgeA: a.YMm + a.HeightMm, edgeB: b.YMm,
                    aLo: a.XMm, aHi: a.XMm + a.WidthMm,
                    bLo: b.XMm, bHi: b.XMm + b.WidthMm);

                var pick = (v, h) switch
                {
                    (null, null) => null,
                    (not null, null) => v,
                    (null, not null) => h,
                    _ => v.Value.Score <= h.Value.Score ? v : h,
                };
                if (pick is { } c) Emit(direct, offBand, c);
            }

        // A direct band from ANY pair beats an off-band mark from another pair on
        // the same seam line — the cursor does cross there, so no ✕ on top of it.
        var result = new List<SeamSegment>(direct);
        foreach (var o in offBand) AddMinusDirect(result, direct, o);
        return result;
    }

    /// <summary>Adds an off-band segment with every same-seam direct band cut out of it.</summary>
    private static void AddMinusDirect(List<SeamSegment> result, List<SeamSegment> direct, SeamSegment o)
    {
        var pieces = new List<(double Lo, double Hi)> { (o.StartMm, o.EndMm) };
        foreach (var d in direct)
        {
            if (d.Vertical != o.Vertical || Math.Abs(d.SeamMm - o.SeamMm) > SeamGapMm) continue;
            var next = new List<(double, double)>();
            foreach (var (lo, hi) in pieces)
            {
                if (d.EndMm <= lo || d.StartMm >= hi) { next.Add((lo, hi)); continue; }
                if (d.StartMm - lo > MinOffBandMm) next.Add((lo, d.StartMm));
                if (hi - d.EndMm > MinOffBandMm) next.Add((d.EndMm, hi));
            }
            pieces = next;
        }
        foreach (var (lo, hi) in pieces)
            if (hi - lo > MinOffBandMm) result.Add(o with { StartMm = lo, EndMm = hi });
    }

    private readonly record struct SeamCandidate(
        bool Vertical, double Seam, double ALo, double AHi, double BLo, double BHi, double Score);

    /// <summary>Evaluates one edge pairing; null when it's outside the router's
    /// adjacency tolerance. Score = seam gap + along-axis distance (smaller = closer).</summary>
    private static SeamCandidate? Candidate(bool vertical,
        double edgeA, double edgeB, double aLo, double aHi, double bLo, double bHi)
    {
        // Same tolerance as the router: a hair of overlap up to a SeamGap of daylight.
        var gap = edgeB - edgeA;
        if (gap < -1 || gap > SeamGapMm) return null;

        var alongDist = Math.Max(0, Math.Max(aLo, bLo) - Math.Min(aHi, bHi));
        return new SeamCandidate(vertical, edgeA + gap / 2, aLo, aHi, bLo, bHi,
            Score: Math.Max(gap, 0) + alongDist);
    }

    private static void Emit(List<SeamSegment> direct, List<SeamSegment> offBand, SeamCandidate c)
    {
        var lo = Math.Max(c.ALo, c.BLo);
        var hi = Math.Min(c.AHi, c.BHi);
        if (hi > lo) direct.Add(new SeamSegment(c.Vertical, c.Seam, lo, hi, Direct: true));

        AddOffBand(offBand, c.Vertical, c.Seam, c.ALo, c.AHi, lo, hi);
        AddOffBand(offBand, c.Vertical, c.Seam, c.BLo, c.BHi, lo, hi);
    }

    /// <summary>The parts of one side's edge span with no counterpart across the seam.</summary>
    private static void AddOffBand(List<SeamSegment> result, bool vertical, double seam,
        double lo, double hi, double directLo, double directHi)
    {
        if (directHi <= directLo) // no facing band at all — the whole edge is off-band
        {
            if (hi - lo > MinOffBandMm) result.Add(new SeamSegment(vertical, seam, lo, hi, Direct: false));
            return;
        }
        if (directLo - lo > MinOffBandMm) result.Add(new SeamSegment(vertical, seam, lo, directLo, Direct: false));
        if (hi - directHi > MinOffBandMm) result.Add(new SeamSegment(vertical, seam, directHi, hi, Direct: false));
    }
}
