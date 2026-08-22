using System.Runtime.InteropServices;

namespace Hydra.Display;

internal static class WindowsDdc
{
    private const uint MonitorDefaultToNearest = 2;
    private const byte InputSelectVcp = 0x60;
    private const uint WmSysCommand = 0x0112;
    private const nuint ScMonitorPower = 0xF170;
    private static readonly nint HwndBroadcast = new(0xffff);

    internal static IReadOnlyList<DisplayCommandResult> Probe()
    {
        if (!OperatingSystem.IsWindows()) return [new("probe DDC/CI", false, "Windows only")];
        var monitors = Enumerate();
        try
        {
            return monitors.Count == 0
                ? [new("probe DDC/CI", false, "No physical monitors were exposed by Windows")]
                : monitors.Select(m => new DisplayCommandResult($"monitor {m.LogicalName}", true, m.Description)).ToList();
        }
        finally { Close(monitors); }
    }

    // Physical monitors this machine is the active source for. Windows drops a monitor from the
    // enumeration once it switches to another input, so this doubles as "who can I still command".
    internal static IReadOnlyList<PhysicalMonitorInfo> Inventory()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var monitors = Enumerate();
        try
        {
            return monitors
                .Select(m => new PhysicalMonitorInfo(m.LogicalName, m.Description, m.LogicalName, ReadInput(m.Handle)))
                .ToList();
        }
        finally { Close(monitors); }
    }

    private static int? ReadInput(nint handle) =>
        GetVCPFeatureAndVCPFeatureReply(handle, InputSelectVcp, out _, out var current, out _)
            ? (int)(current & 0xff)
            : null;

    internal static DisplayCommandResult SetInput(string id, int input)
    {
        var monitors = Enumerate();
        try
        {
            var matches = monitors.Where(m => id == "*"
                || m.Description.Contains(id, StringComparison.OrdinalIgnoreCase)
                || m.LogicalName.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
                return new($"set {id} input {input}", false, "No matching physical monitor");

            var failures = new List<string>();
            foreach (var monitor in matches)
            {
                if (!SetVCPFeature(monitor.Handle, InputSelectVcp, (uint)input))
                    failures.Add($"{monitor.Description}: Win32 error {Marshal.GetLastWin32Error()}");
            }
            return new($"set {id} input {input}", failures.Count == 0,
                failures.Count == 0 ? $"Updated {matches.Count} monitor(s)" : string.Join("; ", failures));
        }
        finally { Close(monitors); }
    }

    internal static DisplayCommandResult SetAllDisplayPower(bool wake)
    {
        var result = SendNotifyMessageW(HwndBroadcast, WmSysCommand, ScMonitorPower, wake ? new nint(-1) : new nint(2));
        return new(wake ? "wake displays" : "sleep displays", result, result ? null : $"Win32 error {Marshal.GetLastWin32Error()}");
    }

    private static List<PhysicalMonitor> Enumerate()
    {
        var result = new List<PhysicalMonitor>();
        _ = EnumDisplayMonitors(nint.Zero, nint.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfoW(hMonitor, ref info)) return true;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0) return true;
            var physical = new PhysicalMonitorNative[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physical)) return true;
            foreach (var item in physical)
                result.Add(new PhysicalMonitor(item.Handle, item.Description, info.DeviceName));
            // Handles intentionally stay open only for this short operation and are closed by the caller below.
            return true;
        }, nint.Zero);
        return result;
    }

    private static void Close(IEnumerable<PhysicalMonitor> monitors)
    {
        foreach (var monitor in monitors) _ = DestroyPhysicalMonitor(monitor.Handle);
    }

    private record PhysicalMonitor(nint Handle, string Description, string LogicalName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitorNative
    {
        public nint Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

#pragma warning disable SYSLIB1054 // structs contain fixed-size strings; classic marshalling is intentional here
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, [Out] PhysicalMonitorNative[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitor(nint monitor);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetVCPFeature(nint monitor, byte code, uint value);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(nint monitor, byte code, out uint type, out uint current, out uint maximum);
    [DllImport("user32.dll", EntryPoint = "SendNotifyMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNotifyMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
#pragma warning restore SYSLIB1054
}
