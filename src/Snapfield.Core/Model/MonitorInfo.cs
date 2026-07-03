using Snapfield.Core.Geometry;

namespace Snapfield.Core.Model;

/// <summary>
/// A single physical monitor attached to some machine, placed on the shared
/// global physical plane.
///
/// Two coordinate spaces meet here:
///   * <see cref="PixelBounds"/> — where this monitor lives in ITS machine's
///     virtual-desktop pixel space (used to talk to the OS on that machine).
///   * <see cref="PhysicalBounds"/> — where this monitor lives on the GLOBAL
///     physical plane in millimetres (used for cross-machine cursor routing).
///
/// The mapping between the two is a pure affine scale, because DPI scaling is
/// neutralised by making every agent process Per-Monitor-V2 DPI aware.
/// </summary>
public sealed record MonitorInfo
{
    /// <summary>Stable id of the machine this monitor is attached to.</summary>
    public required string MachineId { get; init; }

    /// <summary>
    /// Stable id of the monitor within its machine. Prefer an EDID-derived key
    /// (manufacturer + serial) so it survives re-plugging into a different port;
    /// fall back to the GDI device name (e.g. \\.\DISPLAY1).
    /// </summary>
    public required string DeviceId { get; init; }

    /// <summary>Human-friendly label shown in the calibration UI.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Position + size in the owning machine's virtual-desktop pixels.</summary>
    public required PixelRect PixelBounds { get; init; }

    /// <summary>Position + size on the global physical plane, in millimetres.</summary>
    public required PhysicalRect PhysicalBounds { get; init; }

    /// <summary>
    /// Windows display scale factor (1.0 = 100%, 1.5 = 150%). Informational only:
    /// routing math uses physical pixels, so scaling does not enter the transform.
    /// </summary>
    public double DpiScale { get; init; } = 1.0;

    /// <summary>
    /// True when this is a built-in laptop panel (connection reports INTERNAL),
    /// false for a standalone monitor. Drives the device silhouette in the UI and
    /// travels with the monitor in the network handshake.
    /// </summary>
    public bool IsInternal { get; init; }

    /// <summary>Globally-unique key: machine + device.</summary>
    public string Key => $"{MachineId}/{DeviceId}";

    /// <summary>Pixels-per-millimetre along X (native resolution ÷ physical width).</summary>
    public double PixelsPerMmX => PhysicalBounds.WidthMm > 0 ? PixelBounds.Width / PhysicalBounds.WidthMm : 0;

    /// <summary>Pixels-per-millimetre along Y (native resolution ÷ physical height).</summary>
    public double PixelsPerMmY => PhysicalBounds.HeightMm > 0 ? PixelBounds.Height / PhysicalBounds.HeightMm : 0;
}
