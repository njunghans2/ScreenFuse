using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;

namespace Hydra.Platform.MacOs;

[SupportedOSPlatform("macos")]
internal static partial class AgentCommands
{
    private const string Label = "app.screenfuse.agent";
    private const string ShieldLabel = "app.screenfuse.shield";
    private const string PlistFileName = "app.screenfuse.agent.plist";

    [LibraryImport("libc")]
    private static partial uint getuid();

    private static string DomainTarget() => $"gui/{getuid()}";

    internal static void Install()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot determine process path");
        var workingDir = Path.GetDirectoryName(exePath)
            ?? throw new InvalidOperationException("cannot determine working directory");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var agentsDir = Path.Combine(home, "Library", "LaunchAgents");
        var logDir = Path.Combine(home, "Library", "Logs", "ScreenFuse");
        var plistPath = Path.Combine(agentsDir, PlistFileName);

        Directory.CreateDirectory(agentsDir);
        Directory.CreateDirectory(logDir);

        var appBundle = FindAppBundle(exePath);
        if (appBundle != null)
        {
            RemoveQuarantine(appBundle, recursive: true);
            Codesign(appBundle, Label, deep: true);
        }
        else
        {
            RemoveQuarantine(exePath);
            Codesign(exePath, Label);
        }
        var shieldPath = Path.Combine(workingDir, "Resources", "MacShield", "hydra-shield.app");
        if (Directory.Exists(shieldPath))
        {
            RemoveQuarantine(shieldPath, recursive: true);
            Codesign(shieldPath, ShieldLabel);
        }

        // remove any running instance before overwriting the plist
        RunLaunchctl($"bootout {DomainTarget()}/{Label}", tolerateFailure: true);

        File.WriteAllText(plistPath, GeneratePlist(exePath, workingDir, logDir), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        RunLaunchctl($"bootstrap {DomainTarget()} \"{plistPath}\"");
        Console.WriteLine("ScreenFuse agent installed and started.");
    }

    // Quitting has to tell launchd, not just exit.
    //
    // Rewriting the plist is not enough on its own: launchd read KeepAlive when the job was
    // bootstrapped, so a job already loaded with the old <true/> keeps relaunching ScreenFuse no
    // matter what the file on disk says. Booting the job out of the domain ends it now and for the
    // rest of this login session; the LaunchAgent is bootstrapped again at the next login, which is
    // exactly the "quit until I come back" the menu item promises.
    internal static void StopAgent()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            RewritePlistIfStale();
            RunLaunchctl($"bootout {DomainTarget()}/{Label}", tolerateFailure: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not stop the ScreenFuse launch agent: {ex.Message}");
        }
    }

    // Brings an agent installed by an older build up to date, so the next login no longer relaunches
    // ScreenFuse after a clean quit even if the user never reinstalls the startup entry.
    private static void RewritePlistIfStale()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var plistPath = Path.Combine(home, "Library", "LaunchAgents", PlistFileName);
        if (!File.Exists(plistPath)) return;

        var current = File.ReadAllText(plistPath);
        if (current.Contains("SuccessfulExit", StringComparison.Ordinal)) return;

        var exePath = Environment.ProcessPath;
        var workingDir = exePath == null ? null : Path.GetDirectoryName(exePath);
        if (exePath == null || workingDir == null) return;
        var logDir = Path.Combine(home, "Library", "Logs", "ScreenFuse");

        File.WriteAllText(plistPath, GeneratePlist(exePath, workingDir, logDir), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine("Updated the ScreenFuse launch agent so a deliberate quit is no longer undone.");
    }

    internal static void Uninstall()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var plistPath = Path.Combine(home, "Library", "LaunchAgents", PlistFileName);

        if (!File.Exists(plistPath))
        {
            Console.WriteLine("ScreenFuse agent is not installed.");
            return;
        }

        RunLaunchctl($"bootout {DomainTarget()}/{Label}", tolerateFailure: true);
        File.Delete(plistPath);
        Console.WriteLine("ScreenFuse agent removed.");
    }

    internal static void Codesign(string path, string identifier, bool deep = false)
    {
        // --requirements sets a permissive designated requirement: any binary with our bundle identifier
        // is trusted, rather than the default which ties the csreq to the specific binary's CDHash.
        // this makes the TCC accessibility entry survive auto-updates — the stored csreq matches
        // any future binary as long as it's signed with the same identifier.
        var psi = new ProcessStartInfo("/usr/bin/codesign")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--force");
        if (deep) psi.ArgumentList.Add("--deep");
        psi.ArgumentList.Add("--sign");
        psi.ArgumentList.Add("-");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(identifier);
        psi.ArgumentList.Add("--requirements");
        psi.ArgumentList.Add($"=designated => identifier {identifier}");
        psi.ArgumentList.Add(path);
        using var proc = Process.Start(psi);
        proc?.WaitForExit(); // failure is non-fatal
    }

    private static string? FindAppBundle(string executablePath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(executablePath)!);
        while (directory != null)
        {
            if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private static void RemoveQuarantine(string path, bool recursive = false)
    {
        foreach (var attr in new[] { "com.apple.quarantine", "com.apple.provenance" })
        {
            var psi = new ProcessStartInfo("/usr/bin/xattr")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (recursive) psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(attr);
            psi.ArgumentList.Add(path);
            using var proc = Process.Start(psi);
            proc?.WaitForExit(); // failure is fine — attribute may not exist
        }
    }

    private static void RunLaunchctl(string args, bool tolerateFailure = false)
    {
        using var proc = Process.Start(new ProcessStartInfo("/bin/launchctl", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("failed to start launchctl");

        var output = proc.StandardOutput.ReadToEnd();
        var error = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0 && !tolerateFailure)
            throw new InvalidOperationException($"launchctl {args} failed (exit {proc.ExitCode}): {output}{error}");
    }

    private static string GeneratePlist(string exePath, string workingDir, string logDir)
    {
        var exe = SecurityElement.Escape(exePath);
        var wd = SecurityElement.Escape(workingDir);
        var stdout = SecurityElement.Escape(Path.Combine(logDir, "screenfuse.stdout.log"));
        var stderr = SecurityElement.Escape(Path.Combine(logDir, "screenfuse.stderr.log"));

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Label}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{exe}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <!-- Restart only when ScreenFuse dies unexpectedly. A plain <true/> here relaunches it
                     after a clean exit too, so "Quit ScreenFuse" would put the icon straight back. -->
                <key>KeepAlive</key>
                <dict>
                    <key>SuccessfulExit</key>
                    <false/>
                </dict>
                <key>StandardOutPath</key>
                <string>{stdout}</string>
                <key>StandardErrorPath</key>
                <string>{stderr}</string>
                <key>WorkingDirectory</key>
                <string>{wd}</string>
                <key>ThrottleInterval</key>
                <integer>5</integer>
            </dict>
            </plist>
            """;
    }
}
