using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hydra.Display;

// Removes a monitor from — or restores it to — the Windows desktop topology, so a display that has
// switched to another computer stops being part of this one's desktop. Without this, Windows keeps
// the monitor in its arrangement: the desktop extends onto the invisible panel, and windows and the
// pointer keep living where nobody can see them.
[SupportedOSPlatform("windows")]
internal static class WindowsDisplayTopology
{
    // Paths and modes saved at disable time, so an enable restores the monitor at its previous
    // position rather than wherever the system happens to place it. Persisted so a restart between
    // a disable and its enable does not lose the ability to bring the display back.
    private static readonly Dictionary<string, (byte[] Paths, byte[] Modes)> Saved = LoadSaved();

    private static Dictionary<string, (byte[] Paths, byte[] Modes)> LoadSaved()
    {
        try
        {
            var path = SavedPath();
            if (!File.Exists(path)) return new Dictionary<string, (byte[], byte[])>(StringComparer.OrdinalIgnoreCase);
            var raw = File.ReadAllText(path);
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, SavedConfig>>(raw);
            return (parsed ?? []).ToDictionary(p => p.Key,
                p => (Convert.FromBase64String(p.Value.Paths ?? ""), Convert.FromBase64String(p.Value.Modes ?? "")),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) { return new Dictionary<string, (byte[], byte[])>(StringComparer.OrdinalIgnoreCase); }
    }

    private sealed class SavedConfig
    {
        public string? Paths { get; set; }
        public string? Modes { get; set; }
    }

    private static void SaveSaved()
    {
        try
        {
            var payload = Saved.ToDictionary(p => p.Key,
                p => new SavedConfig { Paths = Convert.ToBase64String(p.Value.Paths), Modes = Convert.ToBase64String(p.Value.Modes) },
                StringComparer.OrdinalIgnoreCase);
            var dir = Path.GetDirectoryName(SavedPath())!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SavedPath(), System.Text.Json.JsonSerializer.Serialize(payload));
        }
        catch (Exception) { /* a lost save only costs a manual re-enable */ }
    }

    private static string SavedPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenFuse", "displays-topology.json");

    internal static (bool Success, string Detail) SetMonitorEnabled(string gdiDeviceName, bool enabled)
    {
        if (enabled) return Enable(gdiDeviceName);
        return Disable(gdiDeviceName);
    }

    private static unsafe (bool, string) Disable(string gdiDeviceName)
    {
        if (!QueryActivePaths(out var paths, out var modes)) return (false, "could not query the display topology");

        var keep = new List<DISPLAYCONFIG_PATH_INFO>();
        var removed = false;
        foreach (var path in paths)
        {
            if (SourceGdiName(path).Equals(gdiDeviceName, StringComparison.OrdinalIgnoreCase)) removed = true;
            else keep.Add(path);
        }
        if (!removed) return (true, $"no active path matches {gdiDeviceName} — already disabled or unknown");
        if (keep.Count == 0) return (false, "refusing to disable the last active display");

        // The kept paths keep their current modes (mode index INVALID = leave the mode alone) and no
        // mode array rides along. Passing the queried modes back verbatim is what the API rejects —
        // the mode array from the query is not what SetDisplayConfig accepts back in this form.
        // The full original config is saved first, so the re-enable can restore it exactly.
        Saved[gdiDeviceName] = (MarshalArray(paths), modes);
        SaveSaved();
        for (var i = 0; i < keep.Count; i++)
        {
            var path = keep[i];
            path.SourceInfo.ModeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            path.TargetInfo.ModeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            keep[i] = path;
        }
        var (ok, detail) = SetConfig(keep.ToArray(), []);
        if (!ok)
        {
            Saved.Remove(gdiDeviceName);
            SaveSaved();
            return (false, detail);
        }
        return (true, $"removed {gdiDeviceName} from the desktop topology");
    }

    private static unsafe (bool, string) Enable(string gdiDeviceName)
    {
        // Restore the exact config that was active before the disable — the only form
        // SetDisplayConfig accepts reliably for bringing a removed path back.
        if (Saved.TryGetValue(gdiDeviceName, out var saved))
        {
            Saved.Remove(gdiDeviceName);
            SaveSaved();
            var (ok, detail) = SetConfig(UnmarshalArray<DISPLAYCONFIG_PATH_INFO>(saved.Paths), saved.Modes);
            return (ok, ok ? $"restored {gdiDeviceName} at its previous position" : detail);
        }

        // No saved config: the app restarted between the disable and the enable, or something
        // else took the display off the desktop — Windows itself does, dropping to "Show only on 1"
        // when a monitor changes hands. The removed path cannot be reconstructed from the current
        // query, but it does not have to be: asking Windows to extend across everything attached
        // brings the panel back, which is the whole of what was wanted. Position and resolution are
        // Windows' choice rather than the one it had, and that is worth saying.
        var (extended, why) = ExtendAll();
        return extended
            ? (true, $"{gdiDeviceName} was not disabled by this session, so the desktop was extended across every attached display instead — Windows chose its position.")
            : (false, $"{gdiDeviceName} was not disabled by this session and the desktop could not be extended ({why}) — re-enable it in Windows display settings or reboot.");
    }

    // "Extend these displays", as the Win+P menu means it.
    //
    // Windows drops the desktop to a single display when the monitor a path was driving changes
    // hands, and nothing about a monitor's power state brings it back: the panel is awake and
    // showing this computer, and Windows is simply not rendering to it. Only a topology change
    // does, and this is that change. Path and mode arrays must be null with the topology flags —
    // the whole point is that Windows works the arrangement out itself.
    internal static unsafe (bool Success, string Detail) ExtendAll()
    {
        var result = SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
        return (result == ERROR_SUCCESS,
            result == ERROR_SUCCESS ? "extended the desktop across every attached display"
                : $"SetDisplayConfig(TOPOLOGY_EXTEND) returned {result} (last error {Marshal.GetLastWin32Error()})");
    }

    // Whether something is plugged in that the desktop is not using. Extending is not free — it
    // repositions windows — so it is worth knowing there is a reason before doing it. Comparing the
    // two path queries is enough: every path Windows could light up against the ones it has.
    internal static unsafe bool HasAttachedDisplayThatIsNotOn()
    {
        uint allPaths = 0, allModes = 0, activePaths = 0, activeModes = 0;
        if (GetDisplayConfigBufferSizes(QDC_ALL_PATHS, &allPaths, &allModes) != ERROR_SUCCESS) return false;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &activePaths, &activeModes) != ERROR_SUCCESS) return false;
        return allPaths > activePaths;
    }

    private static unsafe bool QueryActivePaths(out DISPLAYCONFIG_PATH_INFO[] paths, out byte[] modes)
    {
        paths = [];
        modes = [];
        uint pathCount = 0;
        uint modeCount = 0;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount) != ERROR_SUCCESS) return false;
        if (pathCount == 0) return true;

        var pathSize = pathCount * (uint)Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>();
        var modeSize = modeCount * (uint)Marshal.SizeOf<DISPLAYCONFIG_MODE_INFO>();
        var pathBuf = Marshal.AllocHGlobal((int)pathSize);
        var modeBuf = Marshal.AllocHGlobal((int)modeSize);
        try
        {
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, (DISPLAYCONFIG_PATH_INFO*)pathBuf,
                    &modeCount, (DISPLAYCONFIG_MODE_INFO*)modeBuf, null) != ERROR_SUCCESS) return false;

            paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            for (var i = 0; i < pathCount; i++)
                paths[i] = ((DISPLAYCONFIG_PATH_INFO*)pathBuf)[i];

            modes = new byte[modeCount * (uint)Marshal.SizeOf<DISPLAYCONFIG_MODE_INFO>()];
            if (modeCount > 0)
                Buffer.MemoryCopy((void*)modeBuf, (void*)Marshal.UnsafeAddrOfPinnedArrayElement(modes, 0), modes.Length, modes.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(pathBuf);
            Marshal.FreeHGlobal(modeBuf);
        }
        return true;
    }

    private static unsafe (bool, string) SetConfig(DISPLAYCONFIG_PATH_INFO[] paths, byte[] modes)
    {
        fixed (DISPLAYCONFIG_PATH_INFO* pathPtr = paths)
        fixed (byte* modePtr = modes)
        {
            var result = SetDisplayConfig((uint)paths.Length, pathPtr,
                (uint)(modes.Length / Marshal.SizeOf<DISPLAYCONFIG_MODE_INFO>()),
                (DISPLAYCONFIG_MODE_INFO*)modePtr,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES);
            return (result == ERROR_SUCCESS,
                result == ERROR_SUCCESS ? "" : $"SetDisplayConfig returned {result} (last error {Marshal.GetLastWin32Error()})");
        }
    }

    private static unsafe string SourceGdiName(DISPLAYCONFIG_PATH_INFO path)
    {
        var name = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            Header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                Type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                AdapterId = path.SourceInfo.AdapterId,
                Id = path.SourceInfo.Id,
            },
        };
        return DisplayConfigGetDeviceInfo(&name) == ERROR_SUCCESS
            ? new string(name.ViewGdiDeviceName)
            : "";
    }

    private static byte[] MarshalArray<T>(T[] values) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var bytes = new byte[size * values.Length];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var ptr = handle.AddrOfPinnedObject();
            for (var i = 0; i < values.Length; i++)
                Marshal.StructureToPtr(values[i], ptr + i * size, false);
        }
        finally { handle.Free(); }
        return bytes;
    }

    private static T[] UnmarshalArray<T>(byte[] bytes) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var values = new T[bytes.Length / size];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var ptr = handle.AddrOfPinnedObject();
            for (var i = 0; i < values.Length; i++)
                values[i] = Marshal.PtrToStructure<T>(ptr + i * size);
        }
        finally { handle.Free(); }
        return values;
    }

    private const uint ERROR_SUCCESS = 0;
    private const uint QDC_ALL_PATHS = 1;
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint SDC_APPLY = 0x00000080;
    private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    private const uint SDC_ALLOW_CHANGES = 0x00000400;
    private const uint SDC_TOPOLOGY_EXTEND = 0x00000004;
    private const uint DISPLAYCONFIG_PATH_ACTIVE = 1;
    private const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DISPLAYCONFIG_RATIONAL RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO SourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO TargetInfo;
        public uint Flags;
    }

    // The union member's real size is 52 bytes (target modes); 4+4+8+52 = 68.
    [StructLayout(LayoutKind.Sequential, Size = 68)]
    private struct DISPLAYCONFIG_MODE_INFO { private byte _data; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint Type;
        public uint Size;
        public LUID AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public fixed char ViewGdiDeviceName[32];
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe int QueryDisplayConfig(uint flags, uint* pNumPathArrayElements,
        DISPLAYCONFIG_PATH_INFO* pPathInfoArray, uint* pNumModeInfoArrayElements,
        DISPLAYCONFIG_MODE_INFO* pModeInfoArray, uint* pCurrentTopologyId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe int GetDisplayConfigBufferSizes(uint flags, uint* pNumPathArrayElements, uint* pNumModeInfoArrayElements);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe int SetDisplayConfig(uint numPathArrayElements,
        DISPLAYCONFIG_PATH_INFO* pPathInfoArray, uint numModeInfoArrayElements,
        DISPLAYCONFIG_MODE_INFO* pModeInfoArray, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe int DisplayConfigGetDeviceInfo(DISPLAYCONFIG_SOURCE_DEVICE_NAME* requestPacket);
}