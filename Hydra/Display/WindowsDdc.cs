using System.Runtime.InteropServices;

namespace Hydra.Display;

internal static class WindowsDdc
{
    private const byte InputSelectVcp = 0x60;
    private const byte DisplayPowerVcp = 0xD6;
    private const uint OnMode = 0x01;
    private const uint StandbyMode = 0x04;
    private const uint WmSysCommand = 0x0112;
    private const nuint ScMonitorPower = 0xF170;
    private static readonly nint HwndBroadcast = new(0xffff);

    internal static IReadOnlyList<DisplayCommandResult> Probe()
    {
        if (!OperatingSystem.IsWindows()) return [new("probe DDC/CI", false, "Windows only")];
        var monitors = Enumerate();
        try
        {
            if (monitors.Count == 0)
                return [new("probe DDC/CI", false, "No physical monitors were exposed by Windows")];
            return monitors.Select(m =>
            {
                var capabilities = ReadCapabilities(m.Handle);
                var model = ModelOf(capabilities);
                var inputs = SupportedInputs(capabilities);
                var detail = model == null ? m.Description : $"{model} (Windows calls it '{m.Description}')";
                if (ReadInput(m.Handle) is { } current) detail += $", on input {current}";
                if (inputs.Count > 0) detail += $", accepts inputs {string.Join(", ", inputs)}";
                return new DisplayCommandResult($"monitor {m.LogicalName}", true, detail);
            }).ToList();
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
            return monitors.Select(m =>
            {
                var capabilities = ReadCapabilities(m.Handle);
                var model = ModelOf(capabilities);
                return new PhysicalMonitorInfo(
                    m.LogicalName,
                    // Windows names most monitors "Generic PnP Monitor", which is useless for
                    // recognising the same panel from another computer. The monitor's own
                    // capabilities string carries the real model, so prefer it when there is one.
                    model ?? m.Description,
                    m.LogicalName,
                    ReadInput(m.Handle),
                    SupportedInputs(capabilities),
                    model == null ? [m.Description] : [model, m.Description]);
            }).ToList();
        }
        finally { Close(monitors); }
    }

    // MCCS capabilities look like: (prot(monitor)type(lcd)model(FI27Q-X)cmds(01 02)vcp(60(0F 11 12) ...))
    internal static string? ModelOf(string? capabilities) => Section(capabilities, "model");

    // The values VCP 0x60 accepts on this monitor — the inputs it actually has, straight from the
    // monitor, so the settings window can offer real choices instead of a number box.
    internal static IReadOnlyList<int> SupportedInputs(string? capabilities)
    {
        var vcp = Section(capabilities, "vcp");
        if (vcp == null) return [];
        var marker = vcp.IndexOf("60(", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return [];
        var close = vcp.IndexOf(')', marker);
        if (close < 0) return [];
        var values = new List<int>();
        foreach (var token in vcp[(marker + 3)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(token, System.Globalization.NumberStyles.HexNumber, null, out var value) && value is >= 0 and <= 255)
                values.Add(value);
        return values;
    }

    // Reads one balanced "name( ... )" group; nested parentheses are counted so vcp() survives.
    private static string? Section(string? capabilities, string name)
    {
        if (string.IsNullOrEmpty(capabilities)) return null;
        var start = capabilities.IndexOf(name + "(", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var index = start + name.Length + 1;
        var depth = 1;
        for (var i = index; i < capabilities.Length; i++)
        {
            if (capabilities[i] == '(') depth++;
            else if (capabilities[i] == ')' && --depth == 0)
                return capabilities[index..i].Trim();
        }
        return null;
    }

    private static string? ReadCapabilities(nint handle)
    {
        try
        {
            if (!GetCapabilitiesStringLength(handle, out var length) || length is 0 or > 64 * 1024) return null;
            var buffer = new byte[length];
            if (!CapabilitiesRequestAndCapabilitiesReply(handle, buffer, length)) return null;
            return System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0');
        }
        catch (Exception)
        {
            // Plenty of monitors answer VCP but refuse a capabilities request; the description stands.
            return null;
        }
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

    // Puts one physical panel to sleep (VCP 0xD6 = standby) or wakes it (0xD6 = on). The panel
    // blacks out but the display stays part of the desktop — the one case where removing the
    // display would leave the OS with nothing to render, which is how an OS soft-locks.
    internal static DisplayCommandResult SetDisplayStandby(string id, bool standby)
    {
        var monitors = Enumerate();
        try
        {
            var matches = monitors.Where(m => id == "*"
                || m.Description.Contains(id, StringComparison.OrdinalIgnoreCase)
                || m.LogicalName.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
                return new($"set {id} display power", false, "No matching physical monitor");

            var failures = new List<string>();
            foreach (var monitor in matches)
            {
                if (!SetVCPFeature(monitor.Handle, DisplayPowerVcp, standby ? (uint)StandbyMode : OnMode))
                    failures.Add($"{monitor.Description}: Win32 error {Marshal.GetLastWin32Error()}");
            }
            return new($"set {id} display power", failures.Count == 0,
                failures.Count == 0 ? $"Updated {matches.Count} monitor(s)" : string.Join("; ", failures));
        }
        finally { Close(monitors); }
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
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCapabilitiesStringLength(nint monitor, out uint length);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CapabilitiesRequestAndCapabilitiesReply(nint monitor, [Out] byte[] buffer, uint length);
    [DllImport("user32.dll", EntryPoint = "SendNotifyMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNotifyMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
#pragma warning restore SYSLIB1054
}
