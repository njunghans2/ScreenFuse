using Hydra.Config;
using Hydra.Screen;

namespace Hydra.Desk;

// Turns the arranged desk into the crossing edges the input router already understands.
//
// The user arranges physical monitors, not computers. Which computer a monitor shows is a separate,
// per-scene decision, so the edges are derived rather than authored: two monitors on different
// computers become a crossing, and the span they face each other across becomes the percentage
// range, so the pointer comes out where it went in.
public static class DeskArrangement
{
    public record Placed(string MonitorId, string Host, string? ScreenId, int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }

    public static List<Placed> Place(
        IReadOnlyList<DeskMonitorConfig> monitors,
        Func<string, string?> hostFor)
    {
        var placed = new List<Placed>();
        foreach (var monitor in monitors)
        {
            var host = hostFor(monitor.Id);
            if (string.IsNullOrWhiteSpace(host) || monitor.Width <= 0 || monitor.Height <= 0) continue;
            placed.Add(new Placed(monitor.Id, host!, monitor.Source(host!)?.ScreenId,
                monitor.DeskX, monitor.DeskY, monitor.Width, monitor.Height));
        }
        return placed;
    }

    // A crossing names the screen it leaves from and the screen it arrives on.
    //
    // Per computer is not enough. Put one computer's monitor between two of another's, which is a
    // perfectly ordinary desk, and that computer is on the right at one of them and on the left at
    // the other. A single direction per pair of computers cannot say both, so whichever the user
    // meant is lost.
    //
    // The screen comes from the desk, not from the operating system's own arrangement. That is the
    // point: a monitor placed between two others takes the pointer even though the operating system
    // would have moved it to its own next screen. An earlier attempt failed not because naming
    // screens was wrong, but because it named them from which computer was *showing* on each
    // monitor, which is a different question and was usually the wrong answer.
    public static List<HostConfig> BuildHosts(IReadOnlyList<Placed> placed, IEnumerable<string> allHosts)
    {
        var hosts = new Dictionary<string, HostConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in allHosts.Concat(placed.Select(p => p.Host)))
            if (!string.IsNullOrWhiteSpace(name) && !hosts.ContainsKey(name))
                hosts[name] = new HostConfig { Name = name };

        // Which side of which, and how squarely, for every pair of monitors on different computers.
        //
        // Position decides this, not contact. Two monitors with a gap between them are still one to
        // the left of the other, exactly as they sit on the desk, and the pointer should cross
        // between them; requiring them to touch made a perfectly sensible arrangement do nothing.
        // The monitor facing most squarely wins, and the nearer one breaks a tie.
        var best = new Dictionary<(string FromScreen, string To, Direction Dir), (string From, Edge Edge)>();

        void Note(Placed a, Placed b, Direction dir, Edge edge)
        {
            // Keyed by the source screen, so one computer can reach another from more than one of
            // its own monitors, in a different direction from each.
            var key = (a.Host + " " + (a.ScreenId ?? a.MonitorId), b.Host, dir);
            if (best.TryGetValue(key, out var current)
                && (current.Edge.Facing > edge.Facing
                    || (current.Edge.Facing == edge.Facing && current.Edge.Gap <= edge.Gap)))
                return;
            best[key] = (a.Host, edge);
        }

        foreach (var a in placed)
        {
            foreach (var b in placed)
            {
                if (ReferenceEquals(a, b) || a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase)) continue;

                var (top, bottom) = Overlap(a.Y, a.Bottom, b.Y, b.Bottom);
                var (left, right) = Overlap(a.X, a.Right, b.X, b.Right);

                // Side by side: they face each other across whatever vertical span they share.
                if (bottom > top)
                {
                    if (b.X >= a.Right)
                        Note(a, b, Direction.Right, Span(a, b, top, bottom, bottom - top, b.X - a.Right, vertical: true));
                    else if (b.Right <= a.X)
                        Note(a, b, Direction.Left, Span(a, b, top, bottom, bottom - top, a.X - b.Right, vertical: true));
                }

                // Stacked: the same idea turned ninety degrees.
                if (right > left)
                {
                    if (b.Y >= a.Bottom)
                        Note(a, b, Direction.Down, Span(a, b, left, right, right - left, b.Y - a.Bottom, vertical: false));
                    else if (b.Bottom <= a.Y)
                        Note(a, b, Direction.Up, Span(a, b, left, right, right - left, a.Y - b.Bottom, vertical: false));
                }
            }
        }

        foreach (var ((_, to, dir), (from, edge)) in best)
        {
            hosts[from].Neighbours.Add(new NeighbourConfig
            {
                Direction = dir,
                Name = to,
                SourceScreen = edge.SourceScreen,
                DestScreen = edge.DestScreen,
                SourceStart = edge.SourceStart,
                SourceEnd = edge.SourceEnd,
                DestStart = edge.DestStart,
                DestEnd = edge.DestEnd,
                // Both directions are derived independently and precisely; a mirrored guess would
                // land the pointer at the wrong height between monitors of different sizes.
                Mirror = false,
            });
        }

        return hosts.Values.ToList();
    }

    private static (int Start, int End) Overlap(int aStart, int aEnd, int bStart, int bEnd) =>
        (Math.Max(aStart, bStart), Math.Min(aEnd, bEnd));

    // One crossing: which screens, and which part of each facing edge it covers. The shared span is
    // expressed as a percentage of each monitor separately, so the pointer leaves a monitor of one
    // size at the height it enters a monitor of another.
    private record Edge(
        string? SourceScreen, string? DestScreen,
        int SourceStart, int SourceEnd, int DestStart, int DestEnd,
        int Facing, int Gap);

    private static Edge Span(Placed a, Placed b, int start, int end, int facing, int gap, bool vertical)
    {
        var (aOrigin, aSize) = vertical ? (a.Y, a.Height) : (a.X, a.Width);
        var (bOrigin, bSize) = vertical ? (b.Y, b.Height) : (b.X, b.Width);
        return new Edge(
            a.ScreenId, b.ScreenId,
            Percent(start - aOrigin, aSize), Percent(end - aOrigin, aSize),
            Percent(start - bOrigin, bSize), Percent(end - bOrigin, bSize),
            facing, gap);
    }

    private static int Percent(int offset, int size) =>
        size <= 0 ? 0 : Math.Clamp((int)Math.Round(offset * 100.0 / size), 0, 100);
}
