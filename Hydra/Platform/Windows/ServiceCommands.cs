using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Hydra.Config;
using Microsoft.Win32;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class ServiceCommands
{
    private const string ServiceName = "ScreenFuse";
    private const string FirewallRule = "ScreenFuse (Private LAN)";
    private const string SasPolicyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ProductRegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\ScreenFuse";

    internal static void Install()
    {
        EnsureElevated("--install");

        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot determine process path");
        var configPath = Environment.GetEnvironmentVariable("CONFIG") ?? HydraConfigFile.DefaultPath();
        configPath = Path.GetFullPath(configPath);
        if (!File.Exists(configPath))
            throw new FileNotFoundException("Finish ScreenFuse pairing before enabling launch on startup.", configPath);
        Registry.SetValue(ProductRegistryPath, "ConfigPath", configPath, RegistryValueKind.String);

        // remove the "downloaded from internet" mark so windows doesn't block the service binary
        File.Delete(exePath + ":Zone.Identifier");

        if (!RunSc($"create {ServiceName} binPath= \"\\\"{exePath}\\\" --service\" start= auto obj= LocalSystem", tolerateFailure: true))
            RunSc($"config {ServiceName} binPath= \"\\\"{exePath}\\\" --service\" start= auto obj= LocalSystem");
        RunSc($"description {ServiceName} \"ScreenFuse — cross-platform desk and display routing\"");
        RunSc($"failure {ServiceName} reset= 0 actions= restart/5000/restart/5000/restart/5000");
        RunNetsh("advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
        RunNetsh("advfirewall", "firewall", "add", "rule", $"name={FirewallRule}", "dir=in", "action=allow", "profile=private", $"program={exePath}", "enable=yes");

        // required for SendSAS() to work when called from a service
        if (Registry.GetValue(ProductRegistryPath, "PreviousSoftwareSASGeneration", null) == null)
        {
            var previousSas = Registry.GetValue(SasPolicyPath, "SoftwareSASGeneration", null);
            Registry.SetValue(ProductRegistryPath, "PreviousSoftwareSASGeneration", previousSas is int value ? value : -1, RegistryValueKind.DWord);
        }
        Registry.SetValue(
            SasPolicyPath,
            "SoftwareSASGeneration", 1, RegistryValueKind.DWord);

        RunSc($"start {ServiceName}");
        Console.WriteLine("ScreenFuse service installed and started.");
    }

    internal static void Uninstall()
    {
        EnsureElevated("--uninstall");
        RunSc($"stop {ServiceName}");
        RunSc($"delete {ServiceName}");
        RunNetsh("advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
        var previousSas = Registry.GetValue(ProductRegistryPath, "PreviousSoftwareSASGeneration", null);
        if (previousSas is int value)
        {
            if (value >= 0)
                Registry.SetValue(SasPolicyPath, "SoftwareSASGeneration", value, RegistryValueKind.DWord);
            else
            {
                using var policyKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", writable: true);
                policyKey?.DeleteValue("SoftwareSASGeneration", throwOnMissingValue: false);
            }
        }
        Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\ScreenFuse", throwOnMissingSubKey: false);
        Console.WriteLine("ScreenFuse service removed.");
    }

    private static void EnsureElevated(string arg)
    {
        if (IsElevated()) return;

        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot determine process path");

        try
        {
            Process.Start(new ProcessStartInfo(exePath, arg) { Verb = "runas", UseShellExecute = true })?.WaitForExit();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Elevation failed: {ex.Message}");
        }
        Environment.Exit(0);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool RunSc(string args, bool tolerateFailure = false)
    {
        using var proc = Process.Start(new ProcessStartInfo("sc.exe", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("failed to start sc.exe");

        var output = proc.StandardOutput.ReadToEnd();
        var error = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        // stop may fail if already stopped — that's fine
        if (proc.ExitCode != 0 && !tolerateFailure && !args.StartsWith("stop", StringComparison.Ordinal))
            throw new InvalidOperationException($"sc.exe {args} failed (exit {proc.ExitCode}): {output}{error}");
        return proc.ExitCode == 0;
    }

    private static void RunNetsh(params string[] arguments)
    {
        var psi = new ProcessStartInfo("netsh.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start netsh.exe");
        var output = proc.StandardOutput.ReadToEnd();
        var error = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        // Deleting a missing rule is idempotent and returns success; all other failures matter.
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"netsh failed (exit {proc.ExitCode}): {output}{error}");
    }
}
