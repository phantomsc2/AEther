using System.Runtime.InteropServices;

namespace AEther.Vortice;

// Query the OS reference white for the matching GDI display name. A scale of
// one means 80 nits in scRGB. Failures (e.g. remote sessions) use that nominal level.
internal static class DisplayWhiteLevel
{
    const uint ActivePaths = 2;
    public static float GetScale(string deviceName)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(ActivePaths, out var pathCount, out var modeCount) != 0) break;
            var paths = new DisplayPath[pathCount];
            var modes = new DisplayMode[modeCount];
            var status = QueryDisplayConfig(ActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (status == 122) continue; // Topology changed during the query.
            if (status != 0) break;
            foreach (var path in paths.Take((int)pathCount))
            {
                var source = new SourceName
                {
                    Header = new() { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = path.Source.Adapter, Id = path.Source.Id },
                };
                if (GetSourceName(ref source) != 0 || !string.Equals(source.Name, deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                var white = new WhiteLevel
                {
                    Header = new() { Type = 11, Size = (uint)Marshal.SizeOf<WhiteLevel>(), Adapter = path.Target.Adapter, Id = path.Target.Id },
                };
                if (GetWhiteLevel(ref white) == 0 && white.Level >= 1000)
                    return white.Level / 1000f;
            }
        }
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    struct Header { public uint Type, Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential)]
    struct PathSource { public Luid Adapter; public uint Id, ModeIndex, Status; }
    [StructLayout(LayoutKind.Sequential)]
    struct PathTarget
    {
        public Luid Adapter;
        public uint Id, ModeIndex, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator,
            ScanLineOrdering, Available, Status;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct DisplayPath { public PathSource Source; public PathTarget Target; public uint Flags; }
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    struct DisplayMode { [FieldOffset(0)] public uint Type; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SourceName
    {
        public Header Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct WhiteLevel { public Header Header; public uint Level; }

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayPath[] paths,
        ref uint modeCount, [Out] DisplayMode[] modes, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)]
    static extern int GetSourceName(ref SourceName source);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    static extern int GetWhiteLevel(ref WhiteLevel white);
}
