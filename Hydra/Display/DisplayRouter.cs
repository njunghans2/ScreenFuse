using System.Diagnostics;
using Hydra.Config;
using Microsoft.Extensions.Logging;

namespace Hydra.Display;

public sealed class DisplayRouter(ILogger<DisplayRouter> log) : IDisplayRouter
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<DisplayCommandResult>> ApplyAsync(DisplayRoutingConfig routing, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var results = new List<DisplayCommandResult>();

            if (routing.WakeDisplays)
                results.Add(await SetDisplayPowerAsync(wake: true, cancellationToken));

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
                results.Add(await SetDisplayPowerAsync(wake: false, cancellationToken));

            return results;
        }
        finally
        {
            _gate.Release();
        }
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

    private static Task<DisplayCommandResult> SetDisplayPowerAsync(bool wake, CancellationToken cancellationToken)
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
            return new DisplayCommandResult(label, false, ex.Message);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested) TryKill(process);
            process?.Dispose();
        }
    }

    private static void TryKill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch (Exception) { /* best-effort cleanup */ }
    }
}
