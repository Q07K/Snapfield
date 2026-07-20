namespace Snapfield.Core.Geometry;

/// <summary>
/// An axis-aligned rectangle on the global physical plane, in millimetres.
/// (XMm, YMm) is the top-left corner; Y grows downward to match screen space.
/// </summary>
public readonly record struct PhysicalRect(double XMm, double YMm, double WidthMm, double HeightMm)
{
    public double Right => XMm + WidthMm;
    public double Bottom => YMm + HeightMm;
    public PhysicalPoint Center => new(XMm + WidthMm / 2.0, YMm + HeightMm / 2.0);

    public bool Contains(PhysicalPoint p) =>
        p.XMm >= XMm && p.XMm < Right && p.YMm >= YMm && p.YMm < Bottom;

    /// <summary>Clamps a point to lie within (or on the edge of) this rectangle.
    /// NOTE: a point clamped to Right/Bottom is NOT <see cref="Contains"/> —
    /// those edges are exclusive. Use <see cref="ClampInside"/> when the result
    /// must keep hit-testing back to this rectangle.</summary>
    public PhysicalPoint Clamp(PhysicalPoint p) => new(
        Math.Clamp(p.XMm, XMm, Right),
        Math.Clamp(p.YMm, YMm, Bottom));

    /// <summary>Clamps a point so <see cref="Contains"/> holds afterwards: the
    /// exclusive right/bottom edges are pulled in by <paramref name="insetMm"/>.</summary>
    public PhysicalPoint ClampInside(PhysicalPoint p, double insetMm)
    {
        var ix = Math.Min(insetMm, WidthMm / 2);
        var iy = Math.Min(insetMm, HeightMm / 2);
        return new(
            Math.Clamp(p.XMm, XMm, Right - ix),
            Math.Clamp(p.YMm, YMm, Bottom - iy));
    }

    /// <summary>Shortest distance from a point to this rectangle (0 if inside).</summary>
    public double DistanceTo(PhysicalPoint p)
    {
        var dx = Math.Max(Math.Max(XMm - p.XMm, p.XMm - Right), 0);
        var dy = Math.Max(Math.Max(YMm - p.YMm, p.YMm - Bottom), 0);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public override string ToString() => $"[{XMm:0.#},{YMm:0.#} {WidthMm:0.#}x{HeightMm:0.#}mm]";
}
