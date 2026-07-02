using Snapfield.Core.Model;

namespace Snapfield.Core.Transforms;

/// <summary>
/// Result of resolving a global physical point down to a concrete monitor and
/// the pixel coordinates to feed that monitor's machine.
/// </summary>
public readonly record struct CursorLocation(MonitorInfo Monitor, double PixelX, double PixelY)
{
    public string MachineId => Monitor.MachineId;

    /// <summary>Pixel position rounded to the integer coordinates SendInput expects.</summary>
    public (int X, int Y) ToPixelInt() => ((int)Math.Round(PixelX), (int)Math.Round(PixelY));
}
