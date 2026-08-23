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
    // Everything about the desk that every computer is meant to agree on. Compared rather than
    // trusted: a follower that restarted, or that missed the one push it was ever sent, otherwise
    // keeps a stale desk forever with nothing to reveal it.
    public static string Fingerprint(HydraConfigFile file) =>
        $"{Describe(file)}||{DescribeRuntime(file)}||{DescribeEdges(file)}";

    // The crossings, for deciding whether two computers hold the same document. Deliberately not
    // part of SameRuntime: those are different questions, and conflating them means either a desk
    // that restarts whenever it is rearranged, or one whose rearrangement never reaches the other
    // computer at all.
    private static string DescribeEdges(HydraConfigFile file) => string.Join('|', file.Profiles
        .OrderBy(p => p.ProfileName, StringComparer.OrdinalIgnoreCase)
        .Select(p => string.Join(',', Expanded(p)
            .SelectMany(h => h.Neighbours.Select(n =>
                $"{h.Name}>{n.Direction}>{n.Name}>{n.SourceScreen}>{n.DestScreen}>{n.SourceStart}-{n.SourceEnd}>{n.DestStart}-{n.DestEnd}"
                    .ToLowerInvariant()))
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal))));

    private static List<HostConfig> Expanded(HydraConfig profile)
    {
        var hosts = profile.Hosts.Select(h => new HostConfig
        {
            Name = h.Name,
            DeadCorners = h.DeadCorners,
            Neighbours = h.Neighbours.Select(n => new NeighbourConfig
            {
                Direction = n.Direction, Name = n.Name, Mirror = n.Mirror,
                SourceScreen = n.SourceScreen, DestScreen = n.DestScreen,
                SourceStart = n.SourceStart, SourceEnd = n.SourceEnd,
                DestStart = n.DestStart, DestEnd = n.DestEnd,
            }).ToList(),
        }).ToList();
        HydraConfig.ExpandMirrors(hosts);
        return hosts;
    }

    public static bool SameDesk(HydraConfigFile a, HydraConfigFile b) =>
        Describe(a) == Describe(b);

    // Whether the difference is one a restart is needed for.
    //
    // Most of the desk is read live: monitor positions, aliases and learned input codes change as
    // the desk settles, and a computer that restarted for each of those would spend its life
    // restarting — which is exactly what a peer did, bouncing every few seconds as the controller
    // learned another code, and never staying up long enough to be any use. Only what is read once
    // at startup counts: the crossings, who holds the keyboard, and the set of scenes.
    public static bool SameRuntime(HydraConfigFile a, HydraConfigFile b) =>
        DescribeRuntime(a) == DescribeRuntime(b);

    private static string DescribeRuntime(HydraConfigFile file) => string.Join('|', file.Profiles
        .OrderBy(p => p.ProfileName, StringComparer.OrdinalIgnoreCase)
        .Select(p =>
        {
            // Compare mirror-expanded: a layout written with mirrors and read back with them
            // expanded is the same layout, and treating it as a change restarts forever.
            var hosts = p.Hosts.Select(h => new HostConfig
            {
                Name = h.Name,
                DeadCorners = h.DeadCorners,
                Neighbours = h.Neighbours.Select(n => new NeighbourConfig
                {
                    Direction = n.Direction, Name = n.Name, Mirror = n.Mirror,
                    SourceScreen = n.SourceScreen, DestScreen = n.DestScreen,
                    SourceStart = n.SourceStart, SourceEnd = n.SourceEnd,
                    DestStart = n.DestStart, DestEnd = n.DestEnd,
                }).ToList(),
            }).ToList();
            HydraConfig.ExpandMirrors(hosts);

            // Crossings are deliberately absent. They are applied live now, so a rearranged desk
            // needs no restart; only a change of who holds the keyboard does.
            _ = hosts;
            return $"{p.ProfileName}/{p.Controller}";
        }));

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
