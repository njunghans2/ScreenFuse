namespace Hydra.Platform;

// "Reset App" — puts the settings back to nothing while keeping a copy. A desk that has gone wrong
// is otherwise very hard to tell apart from a bug, because the stored desk is exactly what the next
// start builds on. Shared by the --reset CLI and the troubleshooting button in settings.
internal static class SettingsReset
{
    internal static IReadOnlyList<string> Reset(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath)!;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var removed = new List<string>();

        foreach (var name in new[] { Path.GetFileName(configPath), ".screenfuse-scene", ".screenfuse-controller" })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            File.Move(path, Path.Combine(directory, $"{name}.{stamp}.bak"), overwrite: true);
            removed.Add(name);
        }

        return removed;
    }
}
