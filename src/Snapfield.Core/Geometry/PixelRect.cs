namespace Snapfield.Core.Geometry;

/// <summary>
/// A monitor's pixel rectangle inside its owning machine's virtual-desktop
/// coordinate space. Coordinates are PHYSICAL pixels (the process must be
/// Per-Monitor-V2 DPI aware so Windows reports physical, not scaled, pixels).
/// Left/Top may be negative when a monitor sits left of / above the primary.
/// </summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;

    public bool Contains(double x, double y) =>
        x >= Left && x < Right && y >= Top && y < Bottom;

    public override string ToString() => $"[{Left},{Top} {Width}x{Height}px]";
}
