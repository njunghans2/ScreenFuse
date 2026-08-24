using Cathedral.Extensions;
using Hydra.Screen;

namespace Hydra.Config;

public record ConditionState(List<string> ActiveSsids, int ScreenCount, bool? IsPluggedIn = null);

public class HostConfig
{
    public required string Name { get; init; }
    public List<NeighbourConfig> Neighbours { get; init; } = [];
    public int? DeadCorners { get; init; }  // pixel dead zone at screen corners; overrides root-level setting
}

// One physical monitor on the desk, as arranged in the settings window. Position and size are in
// desk coordinates — the shared physical layout, independent of which computer currently drives it.
// Sources record how each computer reaches this monitor: the DDC input that selects it, the id its
// DDC helper answers to, and the name its screen detector reports.
public class DeskMonitorConfig
{
    public required string Id { get; init; }
    public string? Label { get; init; }
    // Every name any computer knows this monitor by. Each operating system names the same panel
    // differently, so this list is how the desk recognises a monitor it has met before from a
    // computer that describes it in other words.
    public List<string> Aliases { get; init; } = [];
    public int DeskX { get; init; }
    public int DeskY { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public List<MonitorSourceConfig> Sources { get; init; } = [];

    // Whether the pointer may cross onto this monitor from another computer. Turned off, the
    // monitor stays on whichever computer drives it and the pointer cannot leave or enter through
    // it. Defaults to on.
    public bool CrossingEnabled { get; init; } = true;

    // True while this monitor is the blanked (slept) last display of a computer — the desk never
    // removes an OS's final display, it blacks the panel out instead, and the monitor stays in
    // this state until a switch brings another display (or itself) back.
    public bool Sleeping { get; init; }

    public MonitorSourceConfig? Source(string host) => Sources.FirstOrDefault(s => s.Host.EqualsIgnoreCase(host));

    // Not a property: a computed property would be serialised into the config file as a field
    // nobody reads and everybody wonders about.
    public string DisplayName() => string.IsNullOrWhiteSpace(Label) ? Id : Label!;

    // Copy with changes. Hand-writing the copy at each call site is how a monitor quietly loses its
    // aliases — and with them its identity, so the next merge splits it in two again.
    public DeskMonitorConfig With(
        string? label = null, int? deskX = null, int? deskY = null,
        int? width = null, int? height = null, List<MonitorSourceConfig>? sources = null,
        bool? crossingEnabled = null, bool? sleeping = null) => new()
    {
        Id = Id,
        Label = label ?? Label,
        Aliases = [.. Aliases],
        DeskX = deskX ?? DeskX,
        DeskY = deskY ?? DeskY,
        Width = width ?? Width,
        Height = height ?? Height,
        Sources = sources ?? Sources,
        CrossingEnabled = crossingEnabled ?? CrossingEnabled,
        Sleeping = sleeping ?? Sleeping,
    };
}

public class MonitorSourceConfig
{
    public required string Host { get; init; }
    public int? Input { get; init; }      // VCP 0x60 value that selects this host on the monitor
    public List<int> AvailableInputs { get; init; } = []; // the codes the monitor told this host it accepts
    public string? DdcId { get; init; }   // identifier this host's DDC helper answers to
    public string? ScreenId { get; init; } // identifier this host's screen detector reports
}

// Which computer a scene puts on a given monitor.
public class MonitorAssignmentConfig
{
    public required string Monitor { get; init; }
    public required string Host { get; init; }
}

public class NeighbourConfig
{
    public required Direction Direction { get; init; }
    public required string Name { get; init; }       // target host
    public string? SourceScreen { get; init; }       // optional: restrict to this local screen identifier
    public string? DestScreen { get; init; }         // optional: target this specific remote screen identifier
    public int SourceStart { get; init; }             // % of source edge (0-100)
    public int SourceEnd { get; init; } = 100;        // % of source edge (0-100)
    public int DestStart { get; init; }               // % of dest edge (0-100)
    public int DestEnd { get; init; } = 100;          // % of dest edge (0-100)
    public bool Mirror { get; init; } = true;         // auto-create the reverse mapping
}

public class EmbeddedStyxServerConfig
{
    public required int Port { get; init; }
    public required string Password { get; init; }
    public string? DiscoveryName { get; init; }
}

// connection config for connecting to an embedded Styx server (local or remote)
public class EmbeddedStyxConfig
{
    public required string Server { get; init; }
    public required string Password { get; init; }
}

public class ScreenDefinition
{
    public string? DisplayName { get; init; }  // matches DetectedScreen.DisplayName (e.g. "DELL U2720Q")
    public string? OutputName { get; init; }   // matches DetectedScreen.OutputName (e.g. "HDMI-1")
    public string? PlatformId { get; init; }   // matches DetectedScreen.PlatformId
    public decimal? MouseScale { get; init; }          // cursor speed multiplier for this screen; overrides root mouseScale
    public decimal? RelativeMouseScale { get; init; }  // relative-mode speed multiplier; overrides root relativeMouseScale
}

// A DDC/CI input command for one physical monitor. Id is platform-specific:
// Windows: physical monitor description (substring) or logical display name (for example \\.\DISPLAY1)
// Linux: ddcutil display number ("1") or bus ("bus:6")
// macOS: m1ddc display number/UUID. "*" selects the first/default display.
public class MonitorInputConfig
{
    public string Id { get; init; } = "*";
    public required int Input { get; init; }
}

// Commands applied locally before all agents restart into this profile/scene.
// DDC input routing is per-monitor; sleep/wake is an all-display fallback for monitors
// whose automatic input detection is more reliable than VCP 0x60.
public class DisplayRoutingConfig
{
    public List<MonitorInputConfig> Inputs { get; init; } = [];
    // Desk-level assignments — "monitor M shows computer H". Resolved against the root monitor
    // table into per-host DDC commands, so the same scene works from whichever computer runs it.
    public List<MonitorAssignmentConfig> Monitors { get; init; } = [];
    public bool WakeDisplays { get; init; }
    public bool SleepDisplays { get; init; }
    public int SettleDelayMs { get; init; } = 500;

    public string? HostFor(string monitorId) =>
        Monitors.FirstOrDefault(m => m.Monitor.EqualsIgnoreCase(monitorId))?.Host;
}

public class HydraConfig
{
    public required Mode Mode { get; init; }
    public string? ProfileName { get; init; }  // displayed when logging which profile is active

    // Name of the computer that owns the keyboard and mouse in this scene. When set it decides the
    // role — that host runs as master, every other host as slave — so one desk document can be shared
    // verbatim between computers and control can move without rewriting each machine's config.
    // When null the explicit per-machine Mode above is used (legacy configs).
    public string? Controller { get; init; }

    public Mode ResolveMode(string localName) =>
        Controller is { Length: > 0 } c ? (c.EqualsIgnoreCase(localName) ? Config.Mode.Master : Config.Mode.Slave) : Mode;
    // master only — ignored in slave mode
    public List<HostConfig> Hosts { get; init; } = [];
    // slave only — scale config is reported to master via ScreenInfoEntry, master applies it when routing to slave screens
    public List<ScreenDefinition> ScreenDefinitions { get; init; } = [];
    public decimal? MouseScale { get; init; }          // slave only — fallback cursor speed multiplier; overridden by per-screen mouseScale
    public decimal? RelativeMouseScale { get; init; }  // slave only — fallback relative-mode cursor speed; overridden by per-screen relativeMouseScale

    public string? NetworkConfig { get; init; }
    public EmbeddedStyxConfig? EmbeddedStyx { get; init; }         // connect to embedded Styx (plain-text alternative to base64 networkConfig)
    public EmbeddedStyxServerConfig? EmbeddedStyxServer { get; init; }  // run an embedded Styx server on this machine

    public bool HideCursor { get; init; } = false;  // master only — hide cursor on inactivity
    public bool RemoteOnly { get; init; } = false;
    public bool SyncScreensaver { get; init; } = true;
    public bool ScreenLockPropagation { get; init; } = false;  // master only (Mac/Windows) — propagate machine lock to connected slaves
    public bool AccelerateMouseWheel { get; init; } = true;
    public int? DeadCorners { get; init; }  // pixel dead zone at screen corners; scaled by screen scale; per-host setting overrides this

    // optional physical-display commands for this named scene
    public DisplayRoutingConfig DisplayRouting { get; init; } = new();

    // master only — sent with each keypress so per-master preferences are honoured on shared slaves.
    // true (default): held printable keys repeat via keycode-less unicode insertion on Mac slaves, avoiding
    // the macOS press-and-hold accent popup. false: repeat by re-pressing the physical key (legacy behaviour).
    public bool UnicodeKeyRepeat { get; init; } = true;

    // optional — if set, this config only activates when all specified conditions are met
    public ConfigConditions? Conditions { get; init; }

    // Used when control is handed over by hand: the scene is unchanged, only who drives it.
    public HydraConfig WithController(string controller) => new()
    {
        Mode = Mode,
        ProfileName = ProfileName,
        Controller = controller,
        Hosts = Hosts,
        ScreenDefinitions = ScreenDefinitions,
        MouseScale = MouseScale,
        RelativeMouseScale = RelativeMouseScale,
        NetworkConfig = NetworkConfig,
        EmbeddedStyx = EmbeddedStyx,
        EmbeddedStyxServer = EmbeddedStyxServer,
        HideCursor = HideCursor,
        RemoteOnly = RemoteOnly,
        SyncScreensaver = SyncScreensaver,
        ScreenLockPropagation = ScreenLockPropagation,
        AccelerateMouseWheel = AccelerateMouseWheel,
        DeadCorners = DeadCorners,
        DisplayRouting = DisplayRouting,
        UnicodeKeyRepeat = UnicodeKeyRepeat,
        Conditions = Conditions,
    };

    public HostConfig? LocalHost(string resolvedName) => Hosts.FirstOrDefault(s => s.Name.EqualsIgnoreCase(resolvedName));

    public IEnumerable<HostConfig> RemoteHosts(string resolvedName) => Hosts.Where(s => !s.Name.EqualsIgnoreCase(resolvedName));

    // true if any profile has effective conditions — if false, the single unconditional profile is always active
    public static bool HasConditions(List<HydraConfig> profiles) => profiles.Any(c => c.Conditions?.IsEmpty == false);

    // true if any profile matches on SSID — determines whether WiFi detection is needed
    public static bool HasSsidConditions(List<HydraConfig> profiles) => profiles.Any(c => c.Conditions?.Ssid != null);

    // true if any profile matches on screen count — determines whether screen count detection is needed
    public static bool HasScreenCountConditions(List<HydraConfig> profiles) => profiles.Any(c => c.Conditions?.ScreenCount != null);

    // true if any profile matches on AC power state — determines whether power detection is needed
    public static bool HasPluggedInConditions(List<HydraConfig> profiles) => profiles.Any(c => c.Conditions?.IsPluggedIn != null);

    // resolves the active profile from the list based on current condition state.
    // if profileOverride is set, that profile is returned unconditionally (ignores conditions).
    // returns null if no profile matches (hydra should idle until conditions change)
    public static HydraConfig? Resolve(List<HydraConfig> profiles, ConditionState state, string? profileOverride = null)
    {
        if (profileOverride != null)
            return profiles.FirstOrDefault(c => c.ProfileName != null && c.ProfileName.EqualsIgnoreCase(profileOverride));

        HydraConfig? fallback = null;

        foreach (var cfg in profiles)
        {
            if (cfg.Conditions == null || cfg.Conditions.IsEmpty)
            {
                fallback = cfg;
                continue;
            }

            // all specified conditions must match (AND logic)
            if (cfg.Conditions.Ssid != null && !state.ActiveSsids.Any(s => s.EqualsIgnoreCase(cfg.Conditions.Ssid)))
                continue;
            if (cfg.Conditions.ScreenCount != null && state.ScreenCount != cfg.Conditions.ScreenCount)
                continue;
            if (cfg.Conditions.IsPluggedIn != null && state.IsPluggedIn != cfg.Conditions.IsPluggedIn)
                continue;

            return cfg;
        }

        return fallback;
    }

    // parses and validates a JSON string — used in tests to exercise validation logic directly
    internal static List<HydraConfig> ParseAndValidate(string json)
    {
        var file = HydraConfigFile.Parse(json, "<test>");
        return file.Profiles;
    }

    // expands mirror neighbours: for each neighbour with Mirror != false, auto-creates the reverse
    // mapping on the target host if one doesn't already exist. target hosts are created if missing.
    internal static void ExpandMirrors(List<HostConfig> hosts)
    {
        // snapshot to avoid iterating while mutating
        var snapshot = hosts.Select(h => (h.Name, Neighbours: h.Neighbours.ToList())).ToList();

        foreach (var (sourceName, neighbours) in snapshot)
        {
            foreach (var n in neighbours)
            {
                if (!n.Mirror) continue;

                var oppositeDir = n.Direction.Opposite();

                // find or create the target host
                var target = hosts.FirstOrDefault(h => h.Name.EqualsIgnoreCase(n.Name));
                if (target is null)
                {
                    target = new HostConfig { Name = n.Name };
                    hosts.Add(target);
                }

                // skip if target already has an explicit reverse mapping back to source
                if (target.Neighbours.Any(r => r.Direction == oppositeDir && r.Name.EqualsIgnoreCase(sourceName)))
                    continue;

                target.Neighbours.Add(new NeighbourConfig
                {
                    Direction = oppositeDir,
                    Name = sourceName,
                    SourceStart = n.DestStart,
                    SourceEnd = n.DestEnd,
                    DestStart = n.SourceStart,
                    DestEnd = n.SourceEnd,
                    SourceScreen = n.DestScreen,
                    DestScreen = n.SourceScreen,
                    Mirror = false,
                });
            }
        }
    }

    internal static void Validate(List<HydraConfig> profiles, string resolvedName, string? profileOverride = null)
    {
        if (profileOverride != null)
        {
            var exists = profiles.Any(c => c.ProfileName != null && c.ProfileName.EqualsIgnoreCase(profileOverride));
            if (!exists)
                throw new InvalidOperationException($"hydra.conf 'profile' override '{profileOverride}' does not match any profile's profileName.");
        }

        // no duplicate profile names
        var names = profiles.Where(c => !string.IsNullOrWhiteSpace(c.ProfileName))
            .GroupBy(c => c.ProfileName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (names != null)
            throw new InvalidOperationException($"hydra.conf has duplicate profile name '{names.Key}'.");

        // Multiple named unconditional profiles are explicit ScreenFuse scenes. Unnamed
        // profiles remain limited to one because they cannot be selected unambiguously.
        var defaults = profiles.Where(c => c.Conditions == null || c.Conditions.IsEmpty).ToList();
        if (defaults.Count > 1 && defaults.Any(c => string.IsNullOrWhiteSpace(c.ProfileName)))
            throw new InvalidOperationException("screenfuse.conf has multiple default profiles; every unconditional scene must have a unique profileName.");

        foreach (var cfg in profiles.Where(c => c.RemoteOnly))
        {
            if (cfg.ResolveMode(resolvedName) != Mode.Master)
                throw new InvalidOperationException("remoteOnly requires mode: Master.");
            var hasRemoteHost = cfg.Hosts.Any(h => !h.Name.EqualsIgnoreCase(resolvedName));
            if (!hasRemoteHost)
                throw new InvalidOperationException("remoteOnly requires at least one remote host in the hosts list.");
        }

        foreach (var cfg in profiles.Where(c => c.Conditions?.IsEmpty == false))
        {
            if (cfg.Conditions!.ScreenCount is < 1)
                throw new InvalidOperationException("screenCount condition must be >= 1.");
        }

        // no two conditional profiles may have identical (ssid, screenCount, isPluggedIn) tuples
        var conditionKeys = profiles
            .Where(c => c.Conditions?.IsEmpty == false)
            .Select(c => (Ssid: c.Conditions!.Ssid?.ToLowerInvariant(), c.Conditions.ScreenCount, c.Conditions.IsPluggedIn))
            .ToList();
        var duplicate = conditionKeys.GroupBy(k => k).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"hydra.conf has duplicate conditions for ssid='{duplicate.Key.Ssid}' screenCount='{duplicate.Key.ScreenCount}' isPluggedIn='{duplicate.Key.IsPluggedIn}'.");

        foreach (var cfg in profiles)
        {
            if (cfg.NetworkConfig == null && cfg.EmbeddedStyx == null && cfg.EmbeddedStyxServer == null)
                throw new InvalidOperationException($"Profile '{cfg.ProfileName ?? "(default)"}' has no relay configured. Add networkConfig, embeddedStyx, or embeddedStyxServer.");
            if (cfg.EmbeddedStyx?.Server.StartsWith("auto://", StringComparison.OrdinalIgnoreCase) == true &&
                string.IsNullOrWhiteSpace(cfg.EmbeddedStyx.Server[7..]))
                throw new InvalidOperationException("embeddedStyx auto discovery requires a desk name, for example auto://studio.");
            if (cfg.EmbeddedStyx?.Server.StartsWith("auto://", StringComparison.OrdinalIgnoreCase) == true && cfg.EmbeddedStyx.Password.Length < 16)
                throw new InvalidOperationException("LAN discovery requires an embeddedStyx password of at least 16 characters.");
            if (cfg.EmbeddedStyxServer is { Port: < 1024 or > 65535 })
                throw new InvalidOperationException("embeddedStyxServer.port must be between 1024 and 65535.");
            if (cfg.EmbeddedStyxServer?.DiscoveryName is { Length: > 64 })
                throw new InvalidOperationException("embeddedStyxServer.discoveryName must be 64 characters or fewer.");
            if (!string.IsNullOrWhiteSpace(cfg.EmbeddedStyxServer?.DiscoveryName) && cfg.EmbeddedStyxServer!.Password.Length < 16)
                throw new InvalidOperationException("LAN discovery requires an embeddedStyxServer password of at least 16 characters.");

            // no duplicate host names within a profile
            var dupHost = cfg.Hosts
                .GroupBy(h => h.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (dupHost != null)
                throw new InvalidOperationException($"hydra.conf has duplicate host name '{dupHost.Key}' in profile '{cfg.ProfileName ?? "(default)"}'.");

            // A profile with a controller is a shared desk document: every computer stores the same
            // text and reads its own role from it, so master-only and slave-only keys legitimately
            // travel together and are simply ignored by whichever machine they do not apply to.
            if (cfg.Controller == null)
            {
                if (cfg.Mode == Mode.Slave && cfg.HideCursor)
                    throw new InvalidOperationException("hideCursor is master-only. Remove it from slave profiles.");
                if (cfg.Mode == Mode.Slave && cfg.ScreenLockPropagation)
                    throw new InvalidOperationException("screenLockPropagation is master-only. Remove it from slave profiles.");
                if (cfg.Mode == Mode.Master && cfg.MouseScale != null)
                    throw new InvalidOperationException("mouseScale is slave-only. Remove it from master profiles.");
                if (cfg.Mode == Mode.Master && cfg.ScreenDefinitions.Count > 0)
                    throw new InvalidOperationException("screenDefinitions is slave-only. Remove it from master profiles.");
            }

            foreach (var assignment in cfg.DisplayRouting.Monitors)
            {
                if (string.IsNullOrWhiteSpace(assignment.Monitor))
                    throw new InvalidOperationException($"Profile '{cfg.ProfileName ?? "(default)"}' has a monitor assignment with an empty monitor id.");
                if (string.IsNullOrWhiteSpace(assignment.Host))
                    throw new InvalidOperationException($"Profile '{cfg.ProfileName ?? "(default)"}' assigns monitor '{assignment.Monitor}' to an empty computer name.");
            }
            var dupAssignment = cfg.DisplayRouting.Monitors
                .GroupBy(m => m.Monitor.Trim(), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (dupAssignment != null)
                throw new InvalidOperationException($"Profile '{cfg.ProfileName ?? "(default)"}' assigns monitor '{dupAssignment.Key}' more than once.");

            foreach (var def in cfg.ScreenDefinitions)
            {
                if (def.DisplayName == null && def.OutputName == null && def.PlatformId == null)
                    throw new InvalidOperationException("A screenDefinition entry has no matching criteria (displayName, outputName, platformId are all null) — it can never match any screen.");
            }


            if (cfg.DisplayRouting.WakeDisplays && cfg.DisplayRouting.SleepDisplays)
                throw new InvalidOperationException($"Profile '{cfg.ProfileName ?? "(default)"}' cannot wake and sleep displays at the same time.");
            if (cfg.DisplayRouting.SettleDelayMs is < 0 or > 10_000)
                throw new InvalidOperationException($"Profile '{cfg.ProfileName ?? "(default)"}' displayRouting.settleDelayMs must be between 0 and 10000.");
            foreach (var input in cfg.DisplayRouting.Inputs)
            {
                if (string.IsNullOrWhiteSpace(input.Id))
                    throw new InvalidOperationException($"Profile '{cfg.ProfileName ?? "(default)"}' has a display input with an empty id.");
                if (input.Input is < 0 or > 255)
                    throw new InvalidOperationException($"Profile '{cfg.ProfileName ?? "(default)"}' display input must be between 0 and 255.");
            }
        }
    }
}
