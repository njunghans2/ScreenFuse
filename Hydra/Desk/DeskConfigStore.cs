using Hydra.Config;
using Hydra.Tray;

namespace Hydra.Desk;

// Reads and writes the desk document, and decides what "shared" means.
//
// A pushed document must not overwrite the things that make a machine itself: its name, its log
// paths, and above all its relay stanza — the computer running the embedded relay keeps running it
// even when control moves elsewhere, otherwise handing over the keyboard would drop the connection
// that carries the handover.
public sealed class DeskConfigStore(string configPath)
{
    public string Path { get; } = configPath;

    public HydraConfigFile Load() => HydraConfigFile.Parse(File.ReadAllText(Path), Path);

    public Task SaveAsync(HydraConfigFile file) => NativeSettingsPersistence.SaveAsync(file, Path);

    public string Serialize(HydraConfigFile file) => NativeSettingsPersistence.SerializeAndValidate(file, Path);

    public static HydraConfigFile Parse(string json, string path) => HydraConfigFile.Parse(json, path);

    // Takes the desk from the incoming document and everything machine-specific from the local one.
    public static HydraConfigFile Merge(HydraConfigFile local, HydraConfigFile incoming)
    {
        var template = local.Profiles.FirstOrDefault();
        var profiles = incoming.Profiles.Select(shared =>
        {
            var mine = local.Profiles.FirstOrDefault(p =>
                string.Equals(p.ProfileName, shared.ProfileName, StringComparison.OrdinalIgnoreCase)) ?? template;
            return new HydraConfig
            {
                // shared — the desk
                ProfileName = shared.ProfileName,
                Controller = shared.Controller,
                Hosts = shared.Hosts,
                DisplayRouting = shared.DisplayRouting,
                Conditions = shared.Conditions,
                HideCursor = shared.HideCursor,
                SyncScreensaver = shared.SyncScreensaver,
                ScreenLockPropagation = shared.ScreenLockPropagation,
                AccelerateMouseWheel = shared.AccelerateMouseWheel,
                UnicodeKeyRepeat = shared.UnicodeKeyRepeat,
                DeadCorners = shared.DeadCorners,

                // local — this machine's identity, connection and pointer tuning
                Mode = mine?.Mode ?? shared.Mode,
                RemoteOnly = mine?.RemoteOnly ?? false,
                NetworkConfig = mine?.NetworkConfig,
                EmbeddedStyx = mine?.EmbeddedStyx,
                EmbeddedStyxServer = mine?.EmbeddedStyxServer,
                MouseScale = mine?.MouseScale,
                RelativeMouseScale = mine?.RelativeMouseScale,
                ScreenDefinitions = mine?.ScreenDefinitions ?? [],
            };
        }).ToList();

        return new HydraConfigFile
        {
            Name = local.Name,
            AutoUpdate = local.AutoUpdate,
            LogLevel = local.LogLevel,
            LockFile = local.LockFile,
            LogFile = local.LogFile,
            SessionLogFile = local.SessionLogFile,
            LogTruncate = local.LogTruncate,
            Profile = local.Profile,
            ControlPort = local.ControlPort,
            DebugShield = local.DebugShield,
            DebugMouse = local.DebugMouse,
            Monitors = incoming.Monitors,
            Profiles = profiles.Count > 0 ? profiles : local.Profiles,
        };
    }

    // True when the pushed desk would not change anything here — used to avoid a restart loop where
    // two computers keep pushing equivalent documents at each other.
    public static bool SameDesk(HydraConfigFile a, HydraConfigFile b) =>
        Describe(a) == Describe(b);

    private static string Describe(HydraConfigFile file) => string.Join('|',
        file.Monitors.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Select(m =>
            $"{m.Id}@{m.DeskX},{m.DeskY},{m.Width}x{m.Height}:{string.Join(',', m.Sources.OrderBy(s => s.Host, StringComparer.OrdinalIgnoreCase).Select(s => $"{s.Host}={s.Input}"))}")
        .Concat(file.Profiles.Select(p =>
            $"{p.ProfileName}/{p.Controller}/{string.Join(',', p.DisplayRouting.Monitors.OrderBy(m => m.Monitor, StringComparer.OrdinalIgnoreCase).Select(m => $"{m.Monitor}={m.Host}"))}")));
}

// Which computer holds the keyboard and mouse right now, independent of what the scenes say.
// Mirrors SceneOverrideStore: a scene defines a controller, and taking control by hand overrides it
// until the next scene switch clears the override again.
public sealed class ControllerOverrideStore(string configPath)
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(configPath)!, ".screenfuse-controller");

    public string? Read()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            var value = File.ReadAllText(Path).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (IOException) { return null; }
    }

    public void Write(string host)
    {
        var temp = Path + ".tmp";
        File.WriteAllText(temp, host + Environment.NewLine);
        File.Move(temp, Path, true);
    }

    public void Clear()
    {
        try { if (File.Exists(Path)) File.Delete(Path); }
        catch (IOException) { /* best effort — a stale override only costs one extra restart */ }
    }
}
