using System.Diagnostics;
using Hydra.Config;
using Microsoft.Extensions.Logging;

namespace Hydra.Display;

public sealed class DisplayRouter(ILogger<DisplayRouter> log) : IDisplayRouter
{
    private static readonly TimeSpan InputCacheLife = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (int? Input, DateTime Read)> _inputCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<DisplayCommandResult>> ApplyAsync(DisplayRoutingConfig routing, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var results = new List<DisplayCommandResult>();

            if (routing.WakeDisplays)
                results.Add(await SetDisplayPowerCoreAsync(wake: true, cancellationToken));

            foreach (var input in routing.Inputs)
            {
                var result = OperatingSystem.IsWindows()
                    ? WindowsDdc.SetInput(input.Id, input.Input)
                    : await SetInputWithHelperAsync(input, cancellationToken);
                results.Add(result);
                if (!result.Success)
                    log.LogWarning("Display command failed: {Command}: {Detail}", result.Command, result.Detail);
            }

            if (routing.SettleDelayMs > 0 && routing.Inputs.Count > 0)
                await Task.Delay(routing.SettleDelayMs, cancellationToken);

            if (routing.SleepDisplays)
                results.Add(await SetDisplayPowerCoreAsync(wake: false, cancellationToken));

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DisplayCommandResult> SetInputAsync(string id, int input, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = OperatingSystem.IsWindows()
                ? WindowsDdc.SetInput(id, input)
                : await SetInputWithHelperAsync(new MonitorInputConfig { Id = id, Input = input }, cancellationToken);
            if (!result.Success)
                log.LogWarning("Display command failed: {Command}: {Detail}", result.Command, result.Detail);
            _inputCache.Remove(id); // the monitor is on a different input now, by definition
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<PhysicalMonitorInfo>> InventoryAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows()) return WindowsDdc.Inventory();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var probe = OperatingSystem.IsMacOS()
                ? await RunAsync("m1ddc", ["display", "list"], "m1ddc display list", cancellationToken)
                : OperatingSystem.IsLinux()
                    ? await RunAsync("ddcutil", ["detect", "--brief"], "ddcutil detect", cancellationToken)
                    : new DisplayCommandResult("inventory", false, "Unsupported operating system");
            if (!probe.Success || string.IsNullOrWhiteSpace(probe.Detail)) return [];
            var monitors = OperatingSystem.IsMacOS() ? ParseM1Ddc(probe.Detail) : ParseDdcutil(probe.Detail);
            return await WithCurrentInputsAsync(monitors, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    // Reading the input back is what lets a computer learn the code that selects it — a computer
    // that can talk to a monitor is by definition looking at its own input. On Windows that is an
    // in-process call; here it costs a subprocess per monitor, so the answer is cached: the value
    // only changes when someone switches the monitor, and then this computer loses it entirely.
    private async Task<List<PhysicalMonitorInfo>> WithCurrentInputsAsync(List<PhysicalMonitorInfo> monitors, CancellationToken cancellationToken)
    {
        var result = new List<PhysicalMonitorInfo>(monitors.Count);
        foreach (var monitor in monitors)
        {
            if (_inputCache.TryGetValue(monitor.Id, out var cached) && DateTime.UtcNow - cached.Read < InputCacheLife)
            {
                result.Add(monitor with { CurrentInput = cached.Input });
                continue;
            }
            var input = await ReadInputAsync(monitor.Id, cancellationToken);
            _inputCache[monitor.Id] = (input, DateTime.UtcNow);
            result.Add(monitor with { CurrentInput = input });
        }
        return result;
    }

    private async Task<int?> ReadInputAsync(string id, CancellationToken cancellationToken)
    {
        DisplayCommandResult probe;
        if (OperatingSystem.IsMacOS())
            probe = await RunAsync("m1ddc", id == "*" ? ["get", "input"] : ["display", id, "get", "input"], "read input", cancellationToken);
        else if (OperatingSystem.IsLinux())
            probe = await RunAsync("ddcutil", ["getvcp", "60", "--brief", "--display", id], "read input", cancellationToken);
        else return null;

        if (!probe.Success || string.IsNullOrWhiteSpace(probe.Detail)) return null;
        return ParseInput(probe.Detail);
    }

    // m1ddc prints just the number. ddcutil --brief prints "VCP 60 SNC x0f".
    internal static int? ParseInput(string output)
    {
        var text = output.Trim();
        if (int.TryParse(text, out var plain) && plain is >= 0 and <= 255) return plain;
        var token = text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (token == null) return null;
        if (token.StartsWith("x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(token[1..], System.Globalization.NumberStyles.HexNumber, null, out var hex) && hex is >= 0 and <= 255)
            return hex;
        return int.TryParse(token, out var value) && value is >= 0 and <= 255 ? value : null;
    }

    // m1ddc display list prints one monitor per line: "[1] BenQ XL2420T (DisplayPort)".
    private static List<PhysicalMonitorInfo> ParseM1Ddc(string output)
    {
        var monitors = new List<PhysicalMonitorInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var close = line.IndexOf(']');
            if (!line.StartsWith('[') || close < 0) continue;
            var id = line[1..close].Trim();
            var description = line[(close + 1)..].Trim();
            if (id.Length > 0) monitors.Add(new PhysicalMonitorInfo(id, description.Length > 0 ? description : id, Aliases: description.Length > 0 ? [description] : []));
        }
        return monitors;
    }

    // ddcutil detect --brief prints stanzas: "Display 1" then indented "   I2C bus: /dev/i2c-6"
    // and "   Monitor: BNQ:BenQ XL2420T:serial".
    private static List<PhysicalMonitorInfo> ParseDdcutil(string output)
    {
        var monitors = new List<PhysicalMonitorInfo>();
        string? id = null;
        string? description = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("Display ", StringComparison.OrdinalIgnoreCase))
            {
                if (id != null) monitors.Add(new PhysicalMonitorInfo(id, description ?? id, Aliases: description != null ? [description] : []));
                id = line["Display ".Length..].Trim();
                description = null;
            }
            else if (line.TrimStart().StartsWith("Monitor:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(':', StringSplitOptions.TrimEntries);
                description = parts.Length >= 3 ? parts[2] : line.Trim();
            }
        }
        if (id != null) monitors.Add(new PhysicalMonitorInfo(id, description ?? id, Aliases: description != null ? [description] : []));
        return monitors;
    }

    public async Task<IReadOnlyList<DisplayCommandResult>> DoctorAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows()) return WindowsDdc.Probe();
        var helper = OperatingSystem.IsMacOS() ? "m1ddc" : "ddcutil";
        var args = OperatingSystem.IsMacOS() ? new[] { "display", "list" } : new[] { "detect", "--brief" };
        return [await RunAsync(helper, args, $"probe {helper}", cancellationToken)];
    }

    private static Task<DisplayCommandResult> SetInputWithHelperAsync(MonitorInputConfig input, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsMacOS())
        {
            var args = input.Id == "*"
                ? new[] { "set", "input", input.Input.ToString() }
                : new[] { "display", input.Id, "set", "input", input.Input.ToString() };
            return RunAsync("m1ddc", args, $"m1ddc display {input.Id} input {input.Input}", cancellationToken);
        }

        if (OperatingSystem.IsLinux())
        {
            var args = new List<string> { "setvcp", "60", input.Input.ToString() };
            if (input.Id.StartsWith("bus:", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--bus");
                args.Add(input.Id[4..]);
            }
            else if (input.Id != "*")
            {
                args.Add("--display");
                args.Add(input.Id);
            }
            return RunAsync("ddcutil", args, $"ddcutil display {input.Id} input {input.Input}", cancellationToken);
        }

        return Task.FromResult(new DisplayCommandResult("set monitor input", false, "Unsupported operating system"));
    }

    public async Task<DisplayCommandResult> SetDisplayPowerAsync(bool wake, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await SetDisplayPowerCoreAsync(wake, cancellationToken); }
        finally { _gate.Release(); }
    }

    private static Task<DisplayCommandResult> SetDisplayPowerCoreAsync(bool wake, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            return Task.FromResult(WindowsDdc.SetAllDisplayPower(wake));
        if (OperatingSystem.IsMacOS())
            return wake
                ? RunAsync("caffeinate", ["-u", "-t", "1"], "wake displays", cancellationToken)
                : RunAsync("pmset", ["displaysleepnow"], "sleep displays", cancellationToken);
        if (OperatingSystem.IsLinux())
            return RunAsync("xset", ["dpms", "force", wake ? "on" : "off"], wake ? "wake displays" : "sleep displays", cancellationToken);
        return Task.FromResult(new DisplayCommandResult(wake ? "wake displays" : "sleep displays", false, "Unsupported operating system"));
    }

    internal static async Task<DisplayCommandResult> RunAsync(string fileName, IReadOnlyList<string> args, string label, CancellationToken cancellationToken)
    {
        Process? process = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? fileName + ".exe" : fileName);
            var executable = File.Exists(bundled) ? bundled : fileName;
            var psi = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();
            var detail = string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(s => s.Length > 0));
            return new DisplayCommandResult(label, process.ExitCode == 0, detail.Length == 0 ? null : detail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new DisplayCommandResult(label, false, "Command timed out after 8 seconds.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DisplayCommandResult(label, false, Explain(fileName, ex));
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested) TryKill(process);
            process?.Dispose();
        }
    }

    // "No such file or directory" is the operating system talking about the helper, not the monitor.
    // Say which helper is missing and how to get it, since nothing about display routing works
    // without it and the raw message sends people looking in the wrong place.
    private static string Explain(string fileName, Exception ex)
    {
        var missing = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }
            || ex.Message.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("cannot find the file", StringComparison.OrdinalIgnoreCase);
        if (!missing) return ex.Message;

        var install = fileName switch
        {
            "m1ddc" => "Install it with: brew install m1ddc",
            "ddcutil" => "Install it with your package manager, for example: sudo apt install ddcutil",
            _ => null,
        };
        return $"'{fileName}' is not installed, so ScreenFuse cannot switch monitor inputs on this computer."
            + (install == null ? "" : $" {install}");
    }

    private static void TryKill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (Exception) { /* best-effort cleanup */ }
    }
}
