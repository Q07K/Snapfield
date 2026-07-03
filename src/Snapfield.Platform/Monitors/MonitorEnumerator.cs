using System.Management;
using Snapfield.Core.Geometry;
using Snapfield.Core.Model;
using Snapfield.Platform.Interop;
using static Snapfield.Platform.Interop.NativeMethods;

namespace Snapfield.Platform.Monitors;

/// <summary>
/// Discovers the monitors physically attached to THIS machine and produces
/// <see cref="MonitorInfo"/> records with real physical sizes read from EDID.
///
/// Pixel bounds come from Win32 (physical pixels, since the process is
/// Per-Monitor-V2 aware). Physical size comes from WMI's EDID data. The two are
/// associated via the PnP instance id shared by both APIs.
///
/// The physical PLACEMENT produced here is only a provisional left-to-right,
/// top-aligned seed — the calibration UI is where the user pins down the true
/// relative positions (offsets, staggering, gaps).
/// </summary>
public sealed class MonitorEnumerator
{
    /// <summary>
    /// Opts the process into Per-Monitor-V2 DPI awareness. Call ONCE at startup,
    /// before any window is created, so Windows reports physical pixels.
    /// </summary>
    public static void EnableDpiAwareness()
    {
        try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { /* older Windows: best-effort, app manifest can cover it instead */ }
    }

    public IReadOnlyList<MonitorInfo> Enumerate()
    {
        var machineId = Environment.MachineName;
        var gdi = EnumerateGdiMonitors();
        var edid = ReadEdidByPnpId();
        var internalNames = DisplayKind.InternalGdiDeviceNames();

        var result = new List<MonitorInfo>();
        foreach (var g in gdi)
        {
            edid.TryGetValue(g.PnpId ?? "", out var e);

            // Physical size: prefer EDID; else estimate at a nominal 96 DPI so the
            // user has something to drag in calibration.
            double widthMm = e.WidthMm > 0 ? e.WidthMm : g.PixelWidth / 96.0 * 25.4;
            double heightMm = e.HeightMm > 0 ? e.HeightMm : g.PixelHeight / 96.0 * 25.4;

            result.Add(new MonitorInfo
            {
                MachineId = machineId,
                DeviceId = g.PnpId ?? g.DeviceName,
                DisplayName = e.FriendlyName ?? g.DeviceName,
                PixelBounds = new PixelRect(g.Left, g.Top, g.PixelWidth, g.PixelHeight),
                // Provisional placement filled in below once all sizes are known.
                PhysicalBounds = new PhysicalRect(0, 0, widthMm, heightMm),
                IsInternal = internalNames.Contains(g.DeviceName),
            });
        }

        return SeedProvisionalLayout(result);
    }

    /// <summary>Lays monitors left-to-right in pixel order, top-aligned, no gaps (a seed only).</summary>
    private static IReadOnlyList<MonitorInfo> SeedProvisionalLayout(List<MonitorInfo> monitors)
    {
        var ordered = monitors.OrderBy(m => m.PixelBounds.Left).ToList();
        double cursorX = 0;
        var seeded = new List<MonitorInfo>(ordered.Count);
        foreach (var m in ordered)
        {
            seeded.Add(m with
            {
                PhysicalBounds = m.PhysicalBounds with { XMm = cursorX, YMm = 0 },
            });
            cursorX += m.PhysicalBounds.WidthMm;
        }
        return seeded;
    }

    // ── Win32 side: pixel bounds + PnP id per monitor ─────────────────────────

    private sealed record GdiMonitor(string DeviceName, int Left, int Top, int PixelWidth, int PixelHeight, bool Primary, string? PnpId);

    private static List<GdiMonitor> EnumerateGdiMonitors()
    {
        var list = new List<GdiMonitor>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
        {
            var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                var pnp = ResolvePnpId(mi.szDevice);
                list.Add(new GdiMonitor(
                    mi.szDevice,
                    mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left,
                    mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                    (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                    pnp));
            }
            return true; // continue enumeration
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// For an adapter device (\\.\DISPLAYn), finds the attached monitor and returns
    /// its PnP instance key "HWID#INSTANCE" (e.g. GSM5B09#5&amp;fc72e51&amp;0&amp;UID4353),
    /// which is the join key against EDID/WMI data.
    /// </summary>
    private static string? ResolvePnpId(string adapterDeviceName)
    {
        var monitor = new DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (!EnumDisplayDevices(adapterDeviceName, 0, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME))
            return null;

        // Interface form: \\?\DISPLAY#GSM5B09#5&fc72e51&0&UID4353#{guid}
        return ExtractPnpKey(monitor.DeviceID);
    }

    private static string? ExtractPnpKey(string deviceInterface)
    {
        if (string.IsNullOrEmpty(deviceInterface)) return null;
        var parts = deviceInterface.Split('#');
        // parts: ["\\?\DISPLAY", "GSM5B09", "5&fc72e51&0&UID4353", "{guid}"]
        if (parts.Length >= 3)
            return $"{parts[1]}#{parts[2]}".ToUpperInvariant();
        return null;
    }

    // ── WMI/EDID side: physical size + friendly name per PnP id ───────────────

    private readonly record struct EdidInfo(double WidthMm, double HeightMm, string? FriendlyName);

    private static Dictionary<string, EdidInfo> ReadEdidByPnpId()
    {
        var map = new Dictionary<string, EdidInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var scope = new ManagementScope(@"\\.\root\wmi");
            scope.Connect();

            var friendly = ReadFriendlyNames(scope);

            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM WmiMonitorBasicDisplayParams"));
            foreach (ManagementObject mo in searcher.Get())
            {
                var instance = (mo["InstanceName"] as string) ?? "";
                var key = PnpKeyFromWmiInstance(instance);
                if (key is null) continue;

                // MaxHorizontal/VerticalImageSize are in centimetres.
                double wMm = ToByte(mo["MaxHorizontalImageSize"]) * 10.0;
                double hMm = ToByte(mo["MaxVerticalImageSize"]) * 10.0;
                friendly.TryGetValue(key, out var name);
                map[key] = new EdidInfo(wMm, hMm, name);
            }
        }
        catch
        {
            // WMI unavailable (rare) — callers fall back to the DPI estimate.
        }
        return map;
    }

    private static Dictionary<string, string> ReadFriendlyNames(ManagementScope scope)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM WmiMonitorID"));
            foreach (ManagementObject mo in searcher.Get())
            {
                var key = PnpKeyFromWmiInstance((mo["InstanceName"] as string) ?? "");
                if (key is null) continue;
                var name = DecodeUShortString(mo["UserFriendlyName"] as ushort[]);
                if (!string.IsNullOrWhiteSpace(name)) names[key] = name;
            }
        }
        catch { /* optional */ }
        return names;
    }

    /// <summary>WMI InstanceName "DISPLAY\GSM5B09\5&amp;..&amp;UID4353_0" -> "GSM5B09#5&amp;..&amp;UID4353".</summary>
    private static string? PnpKeyFromWmiInstance(string instanceName)
    {
        if (string.IsNullOrEmpty(instanceName)) return null;
        var trimmed = instanceName;
        var underscore = trimmed.LastIndexOf('_');
        if (underscore > 0) trimmed = trimmed[..underscore]; // strip trailing _0
        var parts = trimmed.Split('\\');
        if (parts.Length >= 3)
            return $"{parts[1]}#{parts[2]}".ToUpperInvariant();
        return null;
    }

    private static byte ToByte(object? o) => o is null ? (byte)0 : Convert.ToByte(o);

    private static string DecodeUShortString(ushort[]? chars)
    {
        if (chars is null) return "";
        var sb = new System.Text.StringBuilder(chars.Length);
        foreach (var c in chars)
        {
            if (c == 0) break;
            sb.Append((char)c);
        }
        return sb.ToString();
    }
}
