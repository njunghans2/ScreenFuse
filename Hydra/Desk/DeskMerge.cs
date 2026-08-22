using System.Text;
using Hydra.Config;
using Hydra.Relay;

namespace Hydra.Desk;

public record DeskMergeResult(List<DeskMonitorConfig> Monitors, List<DeskMonitorView> Views, bool ConfigChanged);

// Turns what every computer reports about its displays into one desk.
//
// The awkward part of a shared desk is that a monitor showing another computer's input is invisible
// to this one — it is not "a display that is off", it is simply not there. So the desk cannot be
// rebuilt from scratch each round: the config table is the memory of monitors nobody can currently
// see, and a report is treated as proof that the reporting host is the monitor's active source.
// That also lets the input codes learn themselves: a host that can read a monitor back over DDC is
// by definition looking at its own input, so the value it reads is the code that selects it.
//
// The hard problem is recognising one physical panel across computers that name it differently.
// Windows calls a monitor "Generic PnP Monitor" while its own capabilities string says "AORUS" and
// macOS says "AORUS FI27Q-X"; the same monitor is "BenQ XL2420T (DisplayPort)" on one and "BenQ
// XL2420T" on the other. Matching is therefore done on a set of aliases, normalised and compared by
// containment, with the names that identify nothing at all excluded outright.
public static class DeskMerge
{
    // Space between one computer's monitors and the next, when the desk arranges itself. It is
    // presentation only: a crossing follows from one monitor being beside another, not from the two
    // touching, so this can be whatever reads best without affecting whether the pointer can move.
    private const int GroupGap = 160;
    private const int MinAliasLength = 4;

    // Names the operating system hands out when it knows nothing. Two monitors both called
    // "Generic PnP Monitor" are not the same monitor, and treating them as one merges the desk
    // into a single tile.
    private static readonly string[] GenericNames =
    [
        "genericpnpmonitor", "generalpnpmonitor", "pnpmonitor", "defaultmonitor", "digitalflatpanel",
        "monitor", "display", "unknown", "unknowndisplay", "screen",
        "null", "none", "displayport", "hdmi", "usbc", "dvi", "vga",
    ];

    private static bool Same(string? a, string? b) =>
        a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static DeskMergeResult Merge(
        IReadOnlyList<DeskMonitorConfig> known,
        IReadOnlyDictionary<string, DeskInventoryMessage> reports,
        IReadOnlyDictionary<string, string>? pendingHosts = null,
        IReadOnlyList<string>? canonicalHosts = null)
    {
        var working = known.Select(Builder.From).ToList();
        // A desk written by an earlier version, or by a round where a computer renamed its screens,
        // can hold two entries for one panel. Fold them together before anything else, or the
        // duplicates outlive every later fix.
        var changed = Coalesce(working);
        changed |= Canonicalise(working, canonicalHosts);
        // Gather every computer's claim on every monitor first. Which computer is actually *on* a
        // monitor cannot be decided one host at a time: when two computers can both see a monitor,
        // the answer depends on comparing what they say to each other.
        var claims = new Dictionary<string, List<Claim>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (reportedHost, inventory) in reports.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
        {
            var host = Canonical(reportedHost, canonicalHosts);
            var screens = inventory.Screens ?? [];
            var claimedScreens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var monitor in inventory.Monitors ?? [])
            {
                var screen = MatchScreen(monitor, screens, claimedScreens);
                if (screen != null) claimedScreens.Add(screen.ScreenId);
                var aliases = Aliases(monitor, screen);
                var entry = Find(working, host, monitor.DdcId, ScreenKey(screen), aliases);
                if (entry == null)
                {
                    // A monitor this computer can talk to but cannot name, and which matches none of
                    // its screens, cannot be placed on the desk: there is nothing to identify it by
                    // and nothing to size it from. Inventing an entry produces a phantom monitor
                    // beside the real one it is — better to wait until it can be recognised.
                    if (screen == null && !aliases.Any(Names)) continue;
                    entry = Create(working, aliases, ref changed);
                }
                changed |= entry.Apply(host, monitor.DdcId, ScreenKey(screen), aliases, screen, monitor.SupportedInputs);
                Record(claims, entry.Id, new Claim(host, monitor.CurrentInput, ViaDdc: true));
            }

            // Panels that answer no DDC — a laptop display, or a monitor whose helper is missing.
            // They still belong on the desk; they are just not switchable.
            foreach (var screen in screens.Where(s => !claimedScreens.Contains(s.ScreenId)))
            {
                var aliases = Aliases(null, screen);
                var entry = Find(working, host, null, ScreenKey(screen), aliases)
                            ?? Create(working, aliases.Count > 0 ? aliases : [$"{host} display"], ref changed);
                changed |= entry.Apply(host, null, ScreenKey(screen), aliases, screen, null);
                Record(claims, entry.Id, new Claim(host, null, ViaDdc: false));
            }
        }

        // Coalesce again. An entry can be unrecognisable at the start of the round and obvious by
        // the end of it: the Windows side of a monitor is stored as "Generic PnP Monitor" until its
        // report contributes the model name, and only then can it be seen as the panel the Mac
        // already registered under "AORUS FI27Q-X".
        changed |= Coalesce(working, claims);

        var active = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in working)
        {
            if (!claims.TryGetValue(monitor.Id, out var monitorClaims)) continue;
            var (host, learned) = Resolve(monitor, monitorClaims);
            if (host != null) active[monitor.Id] = host;
            if (learned != null) changed |= monitor.Learn(learned.Value.Host, learned.Value.Input);
        }

        changed |= Place(working, active);

        var monitors = working.Select(w => w.Build()).ToList();
        var views = working.Select(w => new DeskMonitorView(
            w.Id,
            w.Label,
            w.DeskX, w.DeskY, w.Width, w.Height,
            pendingHosts?.GetValueOrDefault(w.Id) ?? active.GetValueOrDefault(w.Id),
            w.Sources
                .OrderBy(s => s.Host, StringComparer.OrdinalIgnoreCase)
                .Select(s => new DeskSourceView(s.Host, s.Input, Same(active.GetValueOrDefault(w.Id), s.Host), s.AvailableInputs))
                .ToList()))
            .OrderBy(v => v.DeskX).ThenBy(v => v.DeskY)
            .ToList();

        return new DeskMergeResult(monitors, views, changed);
    }

    private record Claim(string Host, int? CurrentInput, bool ViaDdc);

    private static void Record(Dictionary<string, List<Claim>> claims, string id, Claim claim)
    {
        if (!claims.TryGetValue(id, out var list)) claims[id] = list = [];
        if (!list.Any(c => Same(c.Host, claim.Host))) list.Add(claim);
    }

    // Decides which computer is on a monitor, and whether that teaches us an input code.
    //
    // VCP 0x60 is a property of the monitor, not of the asker: everyone who can read it gets the
    // same answer, the input the monitor is showing. So:
    //
    //   - Exactly one computer can see it → it is the one being shown, and the value it reads is
    //     therefore the code that selects it. This is how codes learn themselves.
    //   - Several can see it (a monitor that keeps inactive inputs alive, or a dock) → the value
    //     they read says which input is live, and the computer whose known code matches is the one
    //     on screen. Nothing new is learned, because the reading no longer identifies the reader.
    //   - Nobody can read it over DDC → the only computer that lists it as a screen is on it.
    private static (string? Host, (string Host, int Input)? Learned) Resolve(Builder monitor, List<Claim> claims)
    {
        var ddc = claims.Where(c => c.ViaDdc).ToList();
        if (ddc.Count == 0)
        {
            var screenOnly = claims.Select(c => c.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return (screenOnly.Count == 1 ? screenOnly[0] : screenOnly.FirstOrDefault(), null);
        }

        if (ddc.Count == 1)
        {
            var only = ddc[0];
            if (only.CurrentInput is not (>= 0 and <= 255)) return (only.Host, null);

            // Some monitors keep talking on an input they are not showing. If the live input is a
            // code another computer already owns, this reading says who is on screen — not who is
            // asking — and learning from it would hand this computer the other one's input.
            var shownHost = monitor.Sources.FirstOrDefault(s => !Same(s.Host, only.Host) && s.Input == only.CurrentInput);
            if (shownHost != null) return (shownHost.Host, null);

            return (only.Host, (only.Host, only.CurrentInput.Value));
        }

        var live = ddc.Select(c => c.CurrentInput).FirstOrDefault(i => i is >= 0 and <= 255);
        if (live == null) return (ddc[0].Host, null);
        var owner = ddc.FirstOrDefault(c => monitor.Sources.FirstOrDefault(s => Same(s.Host, c.Host))?.Input == live);
        return (owner?.Host ?? ddc[0].Host, null);
    }

    // -- identity --------------------------------------------------------------------------------

    private static List<string> Aliases(DeskMonitorReport? monitor, DeskScreenReport? screen)
    {
        var names = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!names.Any(n => Same(n, value))) names.Add(value.Trim());
        }
        if (monitor != null)
        {
            Add(monitor.Description);
            foreach (var alias in monitor.Aliases ?? []) Add(alias);
        }
        Add(screen?.DisplayName);
        return names;
    }

    // A screen identifier that survives the host gaining or losing a display. ScreenName is
    // "host" with one screen and "host:0" with two, so it changes underneath us exactly when the
    // desk is most in flux; the output name and the model name do not.
    private static string? ScreenKey(DeskScreenReport? screen) =>
        screen == null ? null
        : !string.IsNullOrWhiteSpace(screen.Output) ? screen.Output
        : !string.IsNullOrWhiteSpace(screen.DisplayName) ? screen.DisplayName
        : screen.ScreenId;

    internal static string Normalise(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        return builder.ToString();
    }

    internal static bool IsGeneric(string value)
    {
        var normalised = Normalise(value);
        if (normalised.Length < MinAliasLength || GenericNames.Contains(normalised)) return true;

        // "Display 1", "Monitor 2", "Screen 3" — the word for the thing plus a number. It tells you
        // nothing about which panel it is, and the number belongs to one computer's enumeration
        // order, so it means something different on the machine next to it.
        var withoutNumber = normalised.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return withoutNumber.Length != normalised.Length && GenericNames.Contains(withoutNumber);
    }

    // A UUID or serial identifies the panel perfectly to the computer that issued it and says
    // nothing to any other, so it is worth remembering as an alias but is not a name to found a
    // monitor on — a desk built from one shows a row of hex strings beside the real monitors.
    internal static bool IsOpaqueId(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length < 16) return false;
        var hex = 0;
        foreach (var c in trimmed)
        {
            if (c is '-' or '_' or ':') continue;
            if (!char.IsAsciiHexDigit(c)) return false;
            hex++;
        }
        return hex >= 16;
    }

    // Does this alias actually name the monitor to a human, and to another computer?
    private static bool Names(string alias) => !IsGeneric(alias) && !IsOpaqueId(alias);

    // Two names describe the same panel when one contains the other: "AORUS" inside
    // "AORUSFI27QX", "BENQXL2420T" inside "BENQXL2420TDISPLAYPORT".
    internal static bool SameMonitor(IEnumerable<string> a, IEnumerable<string> b)
    {
        var left = a.Where(n => !IsGeneric(n)).Select(Normalise).ToList();
        var right = b.Where(n => !IsGeneric(n)).Select(Normalise).ToList();
        return left.Any(l => right.Any(r => l == r || l.Contains(r) || r.Contains(l)));
    }

    private static Builder? Find(List<Builder> working, string host, string? ddcId, string? screenId, List<string> aliases)
    {
        // An identifier this host already recorded is the strongest match.
        var byId = working.FirstOrDefault(w => w.Sources.Any(s =>
            Same(s.Host, host)
            && ((ddcId != null && Same(s.DdcId, ddcId)) || (screenId != null && Same(s.ScreenId, screenId)))));
        if (byId != null) return byId;

        // Otherwise this may be a panel another computer already registered, or one this computer
        // registered under a name that has since changed.
        return working.FirstOrDefault(w => SameMonitor(w.Aliases, aliases));
    }

    private static Builder Create(List<Builder> working, List<string> aliases, ref bool changed)
    {
        var label = BestLabel(aliases) ?? "Monitor";
        var entry = new Builder { Id = UniqueId(working, label), Label = label };
        entry.Aliases.AddRange(aliases);
        working.Add(entry);
        changed = true;
        return entry;
    }

    // The most useful name wins: anything is better than a name that identifies nothing, and among
    // real names the longer one carries more (a bare "AORUS" loses to "AORUS FI27Q-X", and the
    // model "XL2410T" loses to the fuller "BenQ XL2420T (DisplayPort)").
    internal static string? BestLabel(IEnumerable<string> aliases)
    {
        string? best = null;
        var bestScore = int.MinValue;
        foreach (var alias in aliases)
        {
            if (string.IsNullOrWhiteSpace(alias)) continue;
            var score = (IsGeneric(alias) ? -1000 : 0) + (IsOpaqueId(alias) ? -500 : 0) + alias.Trim().Length;
            if (score <= bestScore) continue;
            bestScore = score;
            best = alias.Trim();
        }
        return best;
    }

    private static string UniqueId(List<Builder> working, string label)
    {
        var slug = Slug(label);
        if (working.All(w => !Same(w.Id, slug))) return slug;
        for (var i = 2; ; i++)
        {
            var candidate = $"{slug}-{i}";
            if (working.All(w => !Same(w.Id, candidate))) return candidate;
        }
    }

    private static string Slug(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "monitor" : slug;
    }

    // Host names arrive from the relay, from the config and from this machine's own name, and they
    // do not always agree on capitalisation. Left alone, "Mac" and "mac" become two computers.
    private static string Canonical(string host, IReadOnlyList<string>? canonicalHosts) =>
        canonicalHosts?.FirstOrDefault(h => Same(h, host)) ?? host;

    private static bool Canonicalise(List<Builder> working, IReadOnlyList<string>? canonicalHosts)
    {
        var changed = false;
        foreach (var monitor in working)
        {
            foreach (var source in monitor.Sources.ToList())
            {
                var canonical = Canonical(source.Host, canonicalHosts);
                if (canonical == source.Host) continue;
                var existing = monitor.Sources.FirstOrDefault(s => s != source && Same(s.Host, canonical));
                if (existing != null)
                {
                    existing.Input ??= source.Input;
                    existing.DdcId ??= source.DdcId;
                    existing.ScreenId ??= source.ScreenId;
                    monitor.Sources.Remove(source);
                }
                else source.Host = canonical;
                changed = true;
            }
        }
        return changed;
    }

    // Folds entries that describe one panel into the first of them, keeping whatever each knew.
    // Claims follow the entry they were made against, so a computer that reported the absorbed
    // entry is still counted as having reported the survivor.
    private static bool Coalesce(List<Builder> working, Dictionary<string, List<Claim>>? claims = null)
    {
        var changed = false;
        for (var i = 0; i < working.Count; i++)
        {
            for (var j = working.Count - 1; j > i; j--)
            {
                if (!SameMonitor(working[i].Aliases, working[j].Aliases)) continue;
                working[i].Absorb(working[j]);
                if (claims != null && claims.Remove(working[j].Id, out var moved))
                    foreach (var claim in moved) Record(claims, working[i].Id, claim);
                working.RemoveAt(j);
                changed = true;
            }
        }
        return changed;
    }

    // -- layout ----------------------------------------------------------------------------------

    private static DeskScreenReport? MatchScreen(DeskMonitorReport monitor, List<DeskScreenReport> screens, HashSet<string> claimed)
    {
        var free = screens.Where(s => !claimed.Contains(s.ScreenId)).ToList();
        return free.FirstOrDefault(s => Same(s.Output, monitor.DdcId))
            ?? free.FirstOrDefault(s => Same(s.ScreenId, monitor.DdcId))
            ?? free.FirstOrDefault(s => s.DisplayName != null && SameMonitor([s.DisplayName], Aliases(monitor, null)))
            ?? (free.Count == 1 ? free[0] : null);
    }

    // Places whatever has no place yet, then makes sure nothing ends up on top of anything else.
    // Screens belonging to the same computer keep the offsets that computer reports, so an existing
    // multi-monitor setup arrives already arranged the way the operating system has it.
    private static bool Place(List<Builder> working, Dictionary<string, string> active)
    {
        var sized = working.Where(w => w.Width > 0 && w.Height > 0).ToList();
        var changed = false;

        var unplaced = sized.Where(w => !w.Placed).ToList();
        if (unplaced.Count > 0)
        {
            var nextX = sized.Where(w => w.Placed).Select(w => w.DeskX + w.Width).DefaultIfEmpty(0).Max();
            if (sized.Any(w => w.Placed)) nextX += GroupGap;

            foreach (var group in unplaced.GroupBy(w => active.GetValueOrDefault(w.Id) ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var members = group.ToList();
                var minX = members.Min(m => m.ScreenX);
                var minY = members.Min(m => m.ScreenY);
                foreach (var member in members)
                {
                    member.DeskX = nextX + (member.ScreenX - minX);
                    member.DeskY = member.ScreenY - minY;
                    member.Placed = true;
                }
                nextX += members.Max(m => m.ScreenX - minX + m.Width) + GroupGap;
            }
            changed = true;
        }

        changed |= Separate(sized);
        return changed;
    }

    // An overlap is never what the user meant, and it makes the arrangement unreadable and the
    // derived crossings nonsense. Push the offender clear along the smaller axis and settle.
    private static bool Separate(List<Builder> monitors)
    {
        var changed = false;
        for (var pass = 0; pass < 8; pass++)
        {
            var moved = false;
            var ordered = monitors.OrderBy(m => m.DeskX).ThenBy(m => m.DeskY).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                for (var j = i + 1; j < ordered.Count; j++)
                {
                    var a = ordered[i];
                    var b = ordered[j];
                    var overlapX = Math.Min(a.DeskX + a.Width, b.DeskX + b.Width) - Math.Max(a.DeskX, b.DeskX);
                    var overlapY = Math.Min(a.DeskY + a.Height, b.DeskY + b.Height) - Math.Max(a.DeskY, b.DeskY);
                    if (overlapX <= 0 || overlapY <= 0) continue;

                    if (overlapX <= overlapY) b.DeskX = a.DeskX + a.Width;
                    else b.DeskY = a.DeskY + a.Height;
                    moved = true;
                    changed = true;
                }
            }
            if (!moved) break;
        }
        return changed;
    }

    private sealed class Builder
    {
        public required string Id { get; init; }
        public string Label { get; set; } = "";
        public List<string> Aliases { get; } = [];
        public int DeskX { get; set; }
        public int DeskY { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Placed { get; set; }
        public int ScreenX { get; set; }
        public int ScreenY { get; set; }
        public List<Source> Sources { get; } = [];

        public static Builder From(DeskMonitorConfig config)
        {
            var builder = new Builder
            {
                Id = config.Id,
                Label = config.Label ?? config.Id,
                DeskX = config.DeskX,
                DeskY = config.DeskY,
                Width = config.Width,
                Height = config.Height,
                Placed = config.Width > 0,
            };
            // Older desks stored no aliases; the label is the only name they knew it by.
            builder.Aliases.AddRange(config.Aliases.Count > 0 ? config.Aliases : [config.Label ?? config.Id]);
            foreach (var source in config.Sources)
                builder.Sources.Add(new Source
                {
                    Host = source.Host, Input = source.Input, DdcId = source.DdcId, ScreenId = source.ScreenId,
                    AvailableInputs = [.. source.AvailableInputs],
                });
            return builder;
        }

        public void Absorb(Builder other)
        {
            foreach (var alias in other.Aliases)
                if (!Aliases.Any(a => string.Equals(a, alias, StringComparison.OrdinalIgnoreCase))) Aliases.Add(alias);
            if (DeskMerge.BestLabel(Aliases) is { } better) Label = better;
            if (Width <= 0) { Width = other.Width; Height = other.Height; }
            if (!Placed && other.Placed) { DeskX = other.DeskX; DeskY = other.DeskY; Placed = true; }

            foreach (var source in other.Sources)
            {
                var mine = Sources.FirstOrDefault(s => string.Equals(s.Host, source.Host, StringComparison.OrdinalIgnoreCase));
                if (mine == null) { Sources.Add(source); continue; }
                mine.Input ??= source.Input;
                mine.DdcId ??= source.DdcId;
                mine.ScreenId ??= source.ScreenId;
                if (mine.AvailableInputs.Count == 0) mine.AvailableInputs = source.AvailableInputs;
            }
        }

        public bool Learn(string host, int input)
        {
            var source = Sources.FirstOrDefault(s => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase));
            if (source == null || source.Input == input) return false;
            source.Input = input;
            return true;
        }

        public bool Apply(
            string host, string? ddcId, string? screenId,
            List<string> aliases, DeskScreenReport? screen, List<int>? supportedInputs)
        {
            var changed = false;
            var source = Sources.FirstOrDefault(s => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase));
            if (source == null)
            {
                source = new Source { Host = host };
                Sources.Add(source);
                changed = true;
            }
            if (ddcId != null && !Same(source.DdcId, ddcId)) { source.DdcId = ddcId; changed = true; }
            if (screenId != null && !Same(source.ScreenId, screenId)) { source.ScreenId = screenId; changed = true; }
            if (supportedInputs is { Count: > 0 } && !source.AvailableInputs.SequenceEqual(supportedInputs))
            {
                source.AvailableInputs = [.. supportedInputs];
                changed = true;
            }

            foreach (var alias in aliases)
                if (!Aliases.Any(a => string.Equals(a, alias, StringComparison.OrdinalIgnoreCase)))
                {
                    Aliases.Add(alias);
                    changed = true;
                }
            if (DeskMerge.BestLabel(Aliases) is { } better && !Same(Label, better)) { Label = better; changed = true; }

            if (screen != null)
            {
                ScreenX = screen.X;
                ScreenY = screen.Y;
                if (Width != screen.Width || Height != screen.Height) { Width = screen.Width; Height = screen.Height; changed = true; }
            }
            return changed;
        }

        public DeskMonitorConfig Build() => new()
        {
            Id = Id,
            Label = Label,
            Aliases = [.. Aliases],
            DeskX = DeskX,
            DeskY = DeskY,
            Width = Width,
            Height = Height,
            Sources = Sources.Select(s => new MonitorSourceConfig
            {
                Host = s.Host, Input = s.Input, DdcId = s.DdcId, ScreenId = s.ScreenId,
                AvailableInputs = [.. s.AvailableInputs],
            }).ToList(),
        };

        public sealed class Source
        {
            public required string Host { get; set; }
            public int? Input { get; set; }
            public List<int> AvailableInputs { get; set; } = [];
            public string? DdcId { get; set; }
            public string? ScreenId { get; set; }
        }
    }
}
