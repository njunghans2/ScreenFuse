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
public static class DeskMerge
{
    private const int GroupGap = 120;

    private static bool Same(string? a, string? b) =>
        a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static DeskMergeResult Merge(
        IReadOnlyList<DeskMonitorConfig> known,
        IReadOnlyDictionary<string, DeskInventoryMessage> reports,
        IReadOnlyDictionary<string, string>? pendingHosts = null)
    {
        var working = known.Select(Builder.From).ToList();
        var changed = false;
        var active = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (host, inventory) in reports.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
        {
            var screens = inventory.Screens ?? [];
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var monitor in inventory.Monitors ?? [])
            {
                var screen = MatchScreen(monitor, screens, claimed);
                if (screen != null) claimed.Add(screen.ScreenId);
                var entry = Find(working, host, monitor.DdcId, screen?.ScreenId, monitor.Description)
                            ?? Create(working, monitor.Description, ref changed);
                changed |= entry.Apply(host, monitor.DdcId, screen?.ScreenId, monitor.CurrentInput, monitor.Description, screen);
                active[entry.Id] = host;
            }

            // Panels that answer no DDC — a laptop display, or a monitor whose helper is missing.
            // They still belong on the desk; they are just not switchable.
            foreach (var screen in screens.Where(s => !claimed.Contains(s.ScreenId)))
            {
                var entry = Find(working, host, null, screen.ScreenId, screen.DisplayName)
                            ?? Create(working, screen.DisplayName ?? screen.Output ?? $"{host} display", ref changed);
                changed |= entry.Apply(host, null, screen.ScreenId, null, screen.DisplayName, screen);
                active[entry.Id] = host;
            }
        }

        changed |= AutoPlace(working, reports, active);

        var monitors = working.Select(w => w.Build()).ToList();
        var views = working.Select(w => new DeskMonitorView(
            w.Id,
            w.Label ?? w.Id,
            w.DeskX, w.DeskY, w.Width, w.Height,
            pendingHosts?.GetValueOrDefault(w.Id) ?? active.GetValueOrDefault(w.Id),
            w.Sources
                .OrderBy(s => s.Host, StringComparer.OrdinalIgnoreCase)
                .Select(s => new DeskSourceView(s.Host, s.Input, Same(active.GetValueOrDefault(w.Id), s.Host)))
                .ToList()))
            .OrderBy(v => v.DeskX).ThenBy(v => v.DeskY)
            .ToList();

        return new DeskMergeResult(monitors, views, changed);
    }

    // A host's DDC helper and its display server name the same panel differently. On Windows both
    // speak \\.\DISPLAYn, which is exact; elsewhere fall back to the model name, then to the only
    // remaining candidate when the host has just one of each.
    private static DeskScreenReport? MatchScreen(DeskMonitorReport monitor, List<DeskScreenReport> screens, HashSet<string> claimed)
    {
        var free = screens.Where(s => !claimed.Contains(s.ScreenId)).ToList();
        return free.FirstOrDefault(s => Same(s.Output, monitor.DdcId))
            ?? free.FirstOrDefault(s => Same(s.ScreenId, monitor.DdcId))
            ?? free.FirstOrDefault(s => s.DisplayName is { Length: > 0 } d && monitor.Description.Contains(d, StringComparison.OrdinalIgnoreCase))
            ?? (free.Count == 1 ? free[0] : null);
    }

    private static Builder? Find(List<Builder> working, string host, string? ddcId, string? screenId, string? description)
    {
        // An identifier this host already recorded is the strongest match.
        var byId = working.FirstOrDefault(w => w.Sources.Any(s =>
            Same(s.Host, host)
            && ((ddcId != null && Same(s.DdcId, ddcId)) || (screenId != null && Same(s.ScreenId, screenId)))));
        if (byId != null) return byId;

        // Otherwise this may be the same physical monitor another computer already registered.
        if (string.IsNullOrWhiteSpace(description)) return null;
        return working.FirstOrDefault(w =>
            Same(w.Label, description) && w.Sources.All(s => !Same(s.Host, host)));
    }

    private static Builder Create(List<Builder> working, string description, ref bool changed)
    {
        var id = UniqueId(working, description);
        var entry = new Builder { Id = id, Label = description };
        working.Add(entry);
        changed = true;
        return entry;
    }

    private static string UniqueId(List<Builder> working, string description)
    {
        var slug = Slug(description);
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

    // New monitors land next to the desk rather than on top of it. Screens belonging to the same
    // computer keep the offsets that computer reports, so an existing multi-monitor setup arrives
    // already arranged the way the operating system has it.
    private static bool AutoPlace(List<Builder> working, IReadOnlyDictionary<string, DeskInventoryMessage> reports, Dictionary<string, string> active)
    {
        var unplaced = working.Where(w => !w.Placed && w.Width > 0).ToList();
        if (unplaced.Count == 0) return false;

        var nextX = working.Where(w => w.Placed).Select(w => w.DeskX + w.Width).DefaultIfEmpty(0).Max();
        if (working.Any(w => w.Placed)) nextX += GroupGap;

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
        return true;
    }

    private sealed class Builder
    {
        public required string Id { get; init; }
        public string Label { get; set; } = "";
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
            foreach (var source in config.Sources)
                builder.Sources.Add(new Source { Host = source.Host, Input = source.Input, DdcId = source.DdcId, ScreenId = source.ScreenId });
            return builder;
        }

        public bool Apply(string host, string? ddcId, string? screenId, int? input, string? label, DeskScreenReport? screen)
        {
            var changed = false;
            var source = Sources.FirstOrDefault(s => Same(s.Host, host));
            if (source == null)
            {
                source = new Source { Host = host };
                Sources.Add(source);
                changed = true;
            }
            if (ddcId != null && !Same(source.DdcId, ddcId)) { source.DdcId = ddcId; changed = true; }
            if (screenId != null && !Same(source.ScreenId, screenId)) { source.ScreenId = screenId; changed = true; }
            // A host reading a monitor back is looking at its own input, so this is the code that selects it.
            if (input is >= 0 and <= 255 && source.Input != input) { source.Input = input; changed = true; }
            if (label is { Length: > 0 } && !Same(Label, label) && Same(Label, Id)) { Label = label; changed = true; }

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
            DeskX = DeskX,
            DeskY = DeskY,
            Width = Width,
            Height = Height,
            Sources = Sources.Select(s => new MonitorSourceConfig { Host = s.Host, Input = s.Input, DdcId = s.DdcId, ScreenId = s.ScreenId }).ToList(),
        };

        public sealed class Source
        {
            public required string Host { get; init; }
            public int? Input { get; set; }
            public string? DdcId { get; set; }
            public string? ScreenId { get; set; }
        }
    }
}
