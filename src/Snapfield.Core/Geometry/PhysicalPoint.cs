namespace Snapfield.Core.Geometry;

/// <summary>
/// A point on the global physical plane, measured in millimetres.
/// The origin (0,0) is an arbitrary but fixed anchor shared by every machine.
/// </summary>
public readonly record struct PhysicalPoint(double XMm, double YMm)
{
    public double DistanceTo(PhysicalPoint other)
    {
        var dx = XMm - other.XMm;
        var dy = YMm - other.YMm;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public override string ToString() => $"({XMm:0.##}mm, {YMm:0.##}mm)";
}
