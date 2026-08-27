namespace Hydra.Platform;

// Startup-state detection for the "Start on startup" toggle. Reports what the operating system
// actually has installed, so the checkbox agrees with the platform even when the entry was created
// somewhere else (pairing, the tray menu, the --install flag).
internal static class StartupState
{
    internal static bool IsInstalled()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
            return Windows.ServiceCommands.IsInstalled();

        if (OperatingSystem.IsMacOS())
            return File.Exists(Path.Combine(home, "Library", "LaunchAgents", "app.screenfuse.agent.plist"));

        if (OperatingSystem.IsLinux())
            return File.Exists(Path.Combine(home, ".config", "autostart", "screenfuse.desktop"))
                || File.Exists(Path.Combine(home, ".config", "systemd", "user", "screenfuse.service"));

        return false;
    }
}
