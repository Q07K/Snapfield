using System.Runtime.InteropServices;

namespace Snapfield.Platform.Monitors;

/// <summary>
/// Determines which displays are built-in laptop panels using QueryDisplayConfig.
/// A path whose target output technology is INTERNAL (or an embedded DisplayPort/
/// UDI panel) is a laptop screen; everything else is a standalone monitor. Results
/// are keyed by GDI device name (\\.\DISPLAYn) to match EnumDisplayMonitors.
/// </summary>
public static class DisplayKind
{
    public static HashSet<string> InternalGdiDeviceNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0)
                return set;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return set;

            for (var i = 0; i < pathCount; i++)
            {
                var tech = paths[i].targetInfo.outputTechnology;
                var isInternal = tech == OUTPUT_TECHNOLOGY_INTERNAL
                                 || tech == OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
                                 || tech == OUTPUT_TECHNOLOGY_UDI_EMBEDDED;
                if (!isInternal) continue;

                var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = paths[i].sourceInfo.adapterId,
                        id = paths[i].sourceInfo.id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref src) == 0 && !string.IsNullOrEmpty(src.viewGdiDeviceName))
                    set.Add(src.viewGdiDeviceName);
            }
        }
        catch { /* older Windows or API failure: treat everything as a monitor */ }
        return set;
    }

    // ── constants ─────────────────────────────────────────────────────────────
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint DEVICE_INFO_GET_SOURCE_NAME = 1;
    private static readonly uint OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;
    private const uint OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;
    private const uint OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13;

    // ── structs ───────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public int LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx;
        public uint outputTechnology; public uint rotation; public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate; public uint scanLineOrdering;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // The mode union is 48 bytes; we never read its contents, only need the size
    // right so the array marshals correctly.
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct DISPLAYCONFIG_MODE_UNION { }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType; public uint id; public LUID adapterId; public DISPLAYCONFIG_MODE_UNION mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type; public uint size; public LUID adapterId; public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);
}
