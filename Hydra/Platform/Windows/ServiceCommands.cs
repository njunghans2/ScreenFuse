using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Hydra.Config;
using Microsoft.Win32;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static partial class ServiceCommands
{
    private const string ServiceName = "ScreenFuse";

    // Stopping has to go through the service manager, not the process.
    //
    // The service restarts after a failure, and killing it counts as one — so quitting by ending the
    // process just delays it. The service stays stopped until it is started again or the machine
    // reboots, which is the same "quit until I come back" the macOS agent gives. Needs an elevated
    // prompt; the caller reports it when it does not.
    internal static bool StopService() =>
        OperatingSystem.IsWindows() && RunSc($"stop {ServiceName}", tolerateFailure: true);

    internal static bool IsInstalled() =>
        OperatingSystem.IsWindows() && RunSc($"query {ServiceName}", tolerateFailure: true);
    private const string FirewallRule = "ScreenFuse (Private LAN)";
    private const string SasPolicyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ProductRegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\ScreenFuse";

    // Where ScreenFuse lives once it is installed.
    //
    // Not wherever the downloaded copy happened to be run from. Registering the service against the
    // download folder made the service only as durable as that folder: emptying Downloads left a
    // service pointing at a binary that no longer existed, which still ran, still restarted itself,
    // and could not be removed by the very executable that had been deleted.
    internal static string InstallDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ScreenFuse");

    internal static void Install()
    {
        EnsureElevated("--install");

        var runningExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot determine process path");
        var configPath = Environment.GetEnvironmentVariable("CONFIG") ?? HydraConfigFile.DefaultPath();
        configPath = Path.GetFullPath(configPath);
        if (!File.Exists(configPath))
            throw new FileNotFoundException("Finish ScreenFuse pairing before enabling launch on startup.", configPath);

        var installDir = InstallDirectory();
        var exePath = CopyPayload(runningExe, installDir);

        Registry.SetValue(ProductRegistryPath, "ConfigPath", configPath, RegistryValueKind.String);
        Registry.SetValue(ProductRegistryPath, "InstallDir", installDir, RegistryValueKind.String);

        // remove the "downloaded from internet" mark so windows doesn't block the service binary
        File.Delete(exePath + ":Zone.Identifier");

        if (!RunSc($"create {ServiceName} binPath= \"\\\"{exePath}\\\" --service\" start= auto obj= LocalSystem", tolerateFailure: true))
            RunSc($"config {ServiceName} binPath= \"\\\"{exePath}\\\" --service\" start= auto obj= LocalSystem");
        RunSc($"description {ServiceName} \"ScreenFuse — cross-platform desk and display routing\"");

        // Restarts twice, then leaves it alone until a minute has passed without incident.
        //
        // Restarting forever is what made ScreenFuse impossible to be rid of: ending the process
        // counts as a failure, so every attempt to kill it brought it back five seconds later and
        // there was no winning from Task Manager. Two restarts still cover the crash this is meant
        // to survive; a third failure inside the same minute is someone trying to stop it.
        RunSc($"failure {ServiceName} reset= 60 actions= restart/5000/restart/5000/none/0");

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
        Console.WriteLine($"ScreenFuse installed to {installDir} and started.");
        Console.WriteLine("The folder you ran this from is no longer needed and can be deleted.");
    }

    // Copies everything beside the executable into the install directory and returns the installed
    // executable. Already running from the install directory is not an error — that is a repair.
    internal static string CopyPayload(string runningExe, string installDir)
    {
        var source = Path.GetDirectoryName(runningExe)
            ?? throw new InvalidOperationException("cannot determine payload directory");
        var installed = Path.Combine(installDir, Path.GetFileName(runningExe));
        if (SameDirectory(source, installDir)) return installed;

        Directory.CreateDirectory(installDir);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(installDir, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
        return installed;
    }

    internal static bool SameDirectory(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    // Removes everything the installer put there, and does it even when the pieces are already
    // half-gone: a service whose binary was deleted, a registry key with no service, a service with
    // no registry key. Stopping because one step reports "not found" is how an uninstaller leaves a
    // machine in exactly the state it was asked to clean up.
    internal static void Uninstall(bool purgeSettings = false)
    {
        EnsureElevated(purgeSettings ? "--uninstall --purge" : "--uninstall");

        RunSc($"stop {ServiceName}", tolerateFailure: true);
        RunSc($"delete {ServiceName}", tolerateFailure: true);
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

        var configPath = Registry.GetValue(ProductRegistryPath, "ConfigPath", null) as string;
        var installDir = Registry.GetValue(ProductRegistryPath, "InstallDir", null) as string ?? InstallDirectory();
        Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\ScreenFuse", throwOnMissingSubKey: false);

        RemoveInstallDirectory(installDir);

        if (purgeSettings) PurgeSettings(configPath);

        Console.WriteLine("ScreenFuse removed. Nothing is left that can start it again.");
    }

    private static void PurgeSettings(string? configPath)
    {
        var settings = configPath == null ? null : Path.GetDirectoryName(configPath);
        if (settings == null || !Directory.Exists(settings)) return;
        try
        {
            Directory.Delete(settings, recursive: true);
            Console.WriteLine($"Removed settings in {settings}.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not remove {settings}: {ex.Message}");
        }
    }

    private static void RemoveInstallDirectory(string installDir)
    {
        if (!Directory.Exists(installDir)) return;

        var runningExe = Environment.ProcessPath;
        var runningFromHere = runningExe != null
            && runningExe.StartsWith(Path.GetFullPath(installDir), StringComparison.OrdinalIgnoreCase);

        if (!runningFromHere)
        {
            try
            {
                Directory.Delete(installDir, recursive: true);
                Console.WriteLine($"Removed {installDir}.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not remove {installDir}: {ex.Message}");
            }
            return;
        }

        // Uninstalling from the copy being uninstalled: Windows will not delete a running
        // executable. Everything else goes now and the rest at the next restart, so the machine is
        // never left holding a folder nobody is ever going to clean up.
        foreach (var file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(file, runningExe, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch (IOException) { /* in use; taken at the restart below */ }
        }
        MoveFileEx(runningExe!, null, MoveFileDelayUntilReboot);
        MoveFileEx(installDir, null, MoveFileDelayUntilReboot);
        Console.WriteLine($"Removed {installDir} (the running copy goes at the next restart).");
    }

    private const int MoveFileDelayUntilReboot = 0x4;

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existingFileName, string? newFileName, int flags);

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
