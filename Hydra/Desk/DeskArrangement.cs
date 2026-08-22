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

    // Crossings are between computers, not between monitors, and they deliberately name no screen.
    //
    // Naming one is what broke the pointer. A crossing is only reachable at the outer edge of the
    // computer's own desktop, and which monitor that is belongs to the operating system, not to the
    // desk: a monitor currently showing another computer is still a live display on this one — the
    // video link stays up even when the panel is showing a different input — so the desk's idea of
    // who owns a monitor says nothing about where a computer's desktop ends. Anchoring a crossing to
    // "the monitor the Mac is on" put it on an edge in the middle of Windows' desktop, where the
    // pointer simply moves to the next Windows screen and never leaves.
    //
    // Leaving the screens unset hands that decision back to the input router, which already picks
    // the outermost screen for the direction out of the live layout.
    public static List<HostConfig> BuildHosts(IReadOnlyList<Placed> placed, IEnumerable<string> allHosts)
    {
        var hosts = new Dictionary<string, HostConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in allHosts.Concat(placed.Select(p => p.Host)))
            if (!string.IsNullOrWhiteSpace(name) && !hosts.ContainsKey(name))
                hosts[name] = new HostConfig { Name = name };

        // How much edge each pair of computers shares, per direction. The longest shared edge is the
        // one the user means by putting those monitors next to each other.
        var shared = new Dictionary<(string From, string To, Direction Dir), int>();
        void Note(string from, string to, Direction dir, int amount)
        {
            var key = (from, to, dir);
            shared[key] = shared.GetValueOrDefault(key) + amount;
        }

        foreach (var a in placed)
        {
            foreach (var b in placed)
            {
                if (ReferenceEquals(a, b) || a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase)) continue;

                if (Touching(a.Right, b.X) && Overlap(a.Y, a.Bottom, b.Y, b.Bottom) is var (top, bottom) && bottom > top)
                    Note(a.Host, b.Host, Direction.Right, bottom - top);
                else if (Touching(a.Bottom, b.Y) && Overlap(a.X, a.Right, b.X, b.Right) is var (left, right) && right > left)
                    Note(a.Host, b.Host, Direction.Down, right - left);
            }
        }

        // One crossing per pair of computers, in the direction they share the most edge. Mirror is
        // on so the way back is derived rather than stated twice — and so a computer with monitors
        // on both sides of another still gets a working return path.
        foreach (var group in shared.GroupBy(e => Unordered(e.Key.From, e.Key.To)))
        {
            var best = group.MaxBy(e => e.Value);
            var (from, to, dir) = best.Key;
            var host = hosts[from];
            if (host.Neighbours.Any(n => n.Name.Equals(to, StringComparison.OrdinalIgnoreCase))) continue;
            if (hosts[to].Neighbours.Any(n => n.Name.Equals(from, StringComparison.OrdinalIgnoreCase))) continue;
            host.Neighbours.Add(new NeighbourConfig { Direction = dir, Name = to, Mirror = true });
        }

        return hosts.Values.ToList();
    }

    private static bool Touching(int edge, int other) => Math.Abs(edge - other) <= Tolerance;

    private static (int Start, int End) Overlap(int aStart, int aEnd, int bStart, int bEnd) =>
        (Math.Max(aStart, bStart), Math.Min(aEnd, bEnd));

    private static (string, string) Unordered(string a, string b) =>
        string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? (a, b) : (b, a);

}
