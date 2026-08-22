using System.Diagnostics;

namespace Hydra.Platform.Linux;

internal static class LinuxServiceCommands
{
    private const string UnitName = "screenfuse.service";

    internal static void Install()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path");
        var config = FindConfig(executable) ?? throw new FileNotFoundException("Put screenfuse.conf or hydra.conf beside the executable or in the current directory before installing.");
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            InstallDesktopAutostart(executable, config);
            return;
        }
        var unitDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user");
        Directory.CreateDirectory(unitDir);
        var unitPath = Path.Combine(unitDir, UnitName);
        var unit = $"""
            [Unit]
            Description=ScreenFuse cross-platform software KVM
            After=graphical-session.target network-online.target

            [Service]
            Type=simple
            ExecStart="{Escape(executable)}"
            Environment="CONFIG={Escape(config)}"
            Restart=on-failure
            RestartSec=3

            [Install]
            WantedBy=default.target
            """;
        File.WriteAllText(unitPath, unit + Environment.NewLine);
        RunSystemctl("daemon-reload");
        RunSystemctl("enable", "--now", UnitName);
        Console.WriteLine($"Installed {unitPath}");
    }

    internal static void Uninstall()
    {
        RunSystemctl("disable", "--now", UnitName, ignoreFailure: true);
        var desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart", "screenfuse.desktop");
        if (File.Exists(desktopPath)) File.Delete(desktopPath);
        var unitPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user", UnitName);
        if (File.Exists(unitPath)) File.Delete(unitPath);
        RunSystemctl("daemon-reload");
        Console.WriteLine("ScreenFuse user service removed.");
    }

    private static void InstallDesktopAutostart(string executable, string config)
    {
        var autostartDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart");
        Directory.CreateDirectory(autostartDir);
        var desktopPath = Path.Combine(autostartDir, "screenfuse.desktop");
        var desktop = $"""
            [Desktop Entry]
            Type=Application
            Name=ScreenFuse
            Comment=Cross-platform desk and display routing
            Exec=env CONFIG={DesktopQuote(config)} {DesktopQuote(executable)}
            Terminal=false
            X-GNOME-Autostart-enabled=true
            """;
        File.WriteAllText(desktopPath, desktop + Environment.NewLine);
        Console.WriteLine($"Installed graphical-session autostart at {desktopPath}");
    }

    private static string DesktopQuote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%")}\"";

    private static string? FindConfig(string executable)
    {
        var configured = Environment.GetEnvironmentVariable("CONFIG") ?? Hydra.Config.HydraConfigFile.DefaultPath();
        if (File.Exists(configured)) return Path.GetFullPath(configured);
        foreach (var name in new[] { "screenfuse.conf", "hydra.conf" })
        {
            var beside = Path.Combine(Path.GetDirectoryName(executable)!, name);
            if (File.Exists(beside)) return Path.GetFullPath(beside);
            var current = Path.Combine(Directory.GetCurrentDirectory(), name);
            if (File.Exists(current)) return Path.GetFullPath(current);
        }
        return null;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%");

    private static void RunSystemctl(params string[] arguments) => RunSystemctl(arguments, false);

    private static void RunSystemctl(string[] arguments, bool ignoreFailure)
    {
        var psi = new ProcessStartInfo("systemctl") { UseShellExecute = false };
        psi.ArgumentList.Add("--user");
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start systemctl");
        process.WaitForExit();
        if (process.ExitCode != 0 && !ignoreFailure)
            throw new InvalidOperationException($"systemctl {string.Join(' ', arguments)} failed with exit code {process.ExitCode}");
    }

    private static void RunSystemctl(string first, string second, string third, bool ignoreFailure) =>
        RunSystemctl([first, second, third], ignoreFailure);
}
