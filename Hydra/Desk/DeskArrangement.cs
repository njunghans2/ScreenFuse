using Hydra.Config;
using Hydra.Screen;

namespace Hydra.Desk;

// Turns the arranged desk into the crossing edges the input router already understands.
//
// The user arranges physical monitors, not computers. Which computer a monitor shows is a separate,
// per-scene decision — so the edges are derived, not authored: two monitors that touch on the desk
// become a crossing only while different computers are on them, and the shared portion of the
// touching edge becomes the percentage range so the pointer comes out where it went in.
public static class DeskArrangement
{
    // Monitors rarely line up to the pixel after a drag, and a one-pixel gap would silently kill a
    // crossing. Anything closer than this counts as touching.
    private const int Tolerance = 24;

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

    public static List<HostConfig> BuildHosts(IReadOnlyList<Placed> placed, IEnumerable<string> allHosts)
    {
        var hosts = new Dictionary<string, HostConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in allHosts.Concat(placed.Select(p => p.Host)))
            if (!string.IsNullOrWhiteSpace(name) && !hosts.ContainsKey(name))
                hosts[name] = new HostConfig { Name = name };

        foreach (var a in placed)
        {
            foreach (var b in placed)
            {
                if (ReferenceEquals(a, b) || a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase)) continue;

                if (Touching(a.Right, b.X) && Overlap(a.Y, a.Bottom, b.Y, b.Bottom) is var (top, bottom) && bottom > top)
                    Link(hosts, a, b, Direction.Right, top, bottom, vertical: true);
                else if (Touching(a.Bottom, b.Y) && Overlap(a.X, a.Right, b.X, b.Right) is var (left, right) && right > left)
                    Link(hosts, a, b, Direction.Down, left, right, vertical: false);
            }
        }

        return hosts.Values.ToList();
    }

    private static bool Touching(int edge, int other) => Math.Abs(edge - other) <= Tolerance;

    private static (int Start, int End) Overlap(int aStart, int aEnd, int bStart, int bEnd) =>
        (Math.Max(aStart, bStart), Math.Min(aEnd, bEnd));

    // Adds the crossing and its return path in one go. Mirror is off because both directions are
    // written explicitly — the shared span maps to a different percentage of each monitor whenever
    // the two are not the same height, and a mirrored guess would land the pointer off-centre.
    private static void Link(
        Dictionary<string, HostConfig> hosts, Placed a, Placed b,
        Direction direction, int start, int end, bool vertical)
    {
        var (aOrigin, aSize) = vertical ? (a.Y, a.Height) : (a.X, a.Width);
        var (bOrigin, bSize) = vertical ? (b.Y, b.Height) : (b.X, b.Width);

        var sourceStart = Percent(start - aOrigin, aSize);
        var sourceEnd = Percent(end - aOrigin, aSize);
        var destStart = Percent(start - bOrigin, bSize);
        var destEnd = Percent(end - bOrigin, bSize);
        if (sourceEnd <= sourceStart || destEnd <= destStart) return;

        hosts[a.Host].Neighbours.Add(new NeighbourConfig
        {
            Direction = direction,
            Name = b.Host,
            SourceScreen = a.ScreenId,
            DestScreen = b.ScreenId,
            SourceStart = sourceStart,
            SourceEnd = sourceEnd,
            DestStart = destStart,
            DestEnd = destEnd,
            Mirror = false,
        });
        hosts[b.Host].Neighbours.Add(new NeighbourConfig
        {
            Direction = direction.Opposite(),
            Name = a.Host,
            SourceScreen = b.ScreenId,
            DestScreen = a.ScreenId,
            SourceStart = destStart,
            SourceEnd = destEnd,
            DestStart = sourceStart,
            DestEnd = sourceEnd,
            Mirror = false,
        });
    }

    private static int Percent(int offset, int size) =>
        size <= 0 ? 0 : Math.Clamp((int)Math.Round(offset * 100.0 / size), 0, 100);
}
