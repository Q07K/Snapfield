using Snapfield.Core.Model;

namespace Snapfield.Core.Input;

/// <summary>Whether control just crossed the local/remote boundary.</summary>
public enum RouteTransition
{
    /// <summary>No change in which side owns the cursor.</summary>
    None,
    /// <summary>Cursor just left this machine for a remote monitor — engage capture.</summary>
    ToRemote,
    /// <summary>Cursor just returned to a local monitor — release capture and warp.</summary>
    ToLocal,
}

/// <summary>
/// Outcome of feeding one movement into the <see cref="CursorRouter"/>.
/// <see cref="Owner"/> is the monitor the virtual cursor now sits on;
/// <see cref="PixelX"/>/<see cref="PixelY"/> are that monitor's pixel coordinates
/// (where to warp the local cursor, or what to send to the remote machine).
/// </summary>
public readonly record struct RouteResult(
    RouteTransition Transition,
    MonitorInfo? Owner,
    double PixelX,
    double PixelY)
{
    public bool HasOwner => Owner is not null;

    public (int X, int Y) PixelInt => ((int)Math.Round(PixelX), (int)Math.Round(PixelY));
}
