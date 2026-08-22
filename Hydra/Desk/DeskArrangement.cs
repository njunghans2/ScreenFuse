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

        // Which side of which, and how squarely — for every pair of monitors on different computers.
        //
        // Position decides this, not contact. Two monitors with a gap between them are still one to
        // the left of the other, exactly as they are on the desk, and the pointer should cross
        // between them; requiring them to touch made a perfectly sensible arrangement do nothing.
        // Facing edge length is what ranks the candidates, with the closer pair breaking a tie.
        var shared = new Dictionary<(string From, string To, Direction Dir), (int Facing, int Gap)>();
        void Note(string from, string to, Direction dir, int facing, int gap)
        {
            var key = (from, to, dir);
            var current = shared.GetValueOrDefault(key);
            shared[key] = (current.Facing + facing, current.Facing == 0 ? gap : Math.Min(current.Gap, gap));
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
                    if (b.X >= a.Right) Note(a.Host, b.Host, Direction.Right, bottom - top, b.X - a.Right);
                    else if (b.Right <= a.X) Note(a.Host, b.Host, Direction.Left, bottom - top, a.X - b.Right);
                }

                // Stacked: same idea, turned ninety degrees.
                if (right > left)
                {
                    if (b.Y >= a.Bottom) Note(a.Host, b.Host, Direction.Down, right - left, b.Y - a.Bottom);
                    else if (b.Bottom <= a.Y) Note(a.Host, b.Host, Direction.Up, right - left, a.Y - b.Bottom);
                }
            }
        }

        // One crossing per pair of computers, in the direction they share the most edge. Mirror is
        // on so the way back is derived rather than stated twice — and so a computer with monitors
        // on both sides of another still gets a working return path.
        foreach (var group in shared.GroupBy(e => Unordered(e.Key.From, e.Key.To)))
        {
            var best = group
                .OrderByDescending(e => e.Value.Facing)
                .ThenBy(e => e.Value.Gap)
                .First();
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
