using Hydra.Config;
using Hydra.Screen;

namespace Hydra.Pairing;

internal static class PairedDeskConfig
{
    internal static HydraConfigFile Create(
        string localHost,
        string remoteHost,
        bool localIsMaster,
        string deskName,
        string relaySecret,
        Direction remoteDirection) =>
        CreateCore(localHost, remoteHost, localIsMaster, deskName, relaySecret, remoteDirection);

    internal static HydraConfigFile Create(
        string localHost,
        string remoteHost,
        bool localIsMaster,
        string deskName,
        string relaySecret,
        PairingDesktopLayout? localLayout = null,
        PairingDesktopLayout? remoteLayout = null)
    {
        var remoteDirection = InferDirection(localLayout, remoteLayout);
        return CreateCore(localHost, remoteHost, localIsMaster, deskName, relaySecret, remoteDirection);
    }

    private static HydraConfigFile CreateCore(
        string localHost,
        string remoteHost,
        bool localIsMaster,
        string deskName,
        string relaySecret,
        Direction remoteDirection)
    {
        var profile = new HydraConfig
        {
            ProfileName = "Default",
            Mode = localIsMaster ? Mode.Master : Mode.Slave,
            EmbeddedStyxServer = localIsMaster
                ? new EmbeddedStyxServerConfig { Port = 5000, Password = relaySecret, DiscoveryName = deskName }
                : null,
            EmbeddedStyx = localIsMaster
                ? null
                : new EmbeddedStyxConfig { Server = $"auto://{deskName}", Password = relaySecret },
            Hosts = localIsMaster
                ?
                [
                    new HostConfig
                    {
                        Name = localHost,
                        Neighbours = [new NeighbourConfig { Name = remoteHost, Direction = remoteDirection, Mirror = true }],
                    },
                    new HostConfig { Name = remoteHost },
                ]
                : [],
        };
        return new HydraConfigFile { Name = localHost, LockFile = ".screenfuse.lock", Profiles = [profile] };
    }

    // The operating systems already know the relative positions of displays connected
    // to each computer. When one side has an additional display, use that geometry to
    // select the natural crossing edge. Fully overlapping layouts have no positional
    // information between hosts, so retain the conventional right-side fallback; the
    // native settings window remains available for that uncommon ambiguity.
    private static Direction InferDirection(PairingDesktopLayout? localLayout, PairingDesktopLayout? remoteLayout)
    {
        var local = Normalize(localLayout?.Screens);
        var remote = Normalize(remoteLayout?.Screens);
        if (local.Count == 0 || remote.Count == 0) return Direction.Right;

        var localKeys = local.Select(Key).ToHashSet(StringComparer.Ordinal);
        var remoteOnly = remote.Where(s => !localKeys.Contains(Key(s))).ToList();
        if (remoteOnly.Count == 0) return Direction.Right;

        var target = remoteOnly[0];
        var source = local.MinBy(s => Math.Abs((s.X + s.Width / 2) - (target.X + target.Width / 2))
            + Math.Abs((s.Y + s.Height / 2) - (target.Y + target.Height / 2)))!;
        var dx = (target.X + target.Width / 2) - (source.X + source.Width / 2);
        var dy = (target.Y + target.Height / 2) - (source.Y + source.Height / 2);
        return Math.Abs(dx) >= Math.Abs(dy)
            ? dx >= 0 ? Direction.Right : Direction.Left
            : dy >= 0 ? Direction.Down : Direction.Up;
    }

    private static List<PairingScreenBounds> Normalize(List<PairingScreenBounds>? screens)
    {
        if (screens is not { Count: > 0 }) return [];
        var minX = screens.Min(s => s.X);
        var minY = screens.Min(s => s.Y);
        return screens.Select(s => new PairingScreenBounds(s.X - minX, s.Y - minY, s.Width, s.Height)).ToList();
    }

    private static string Key(PairingScreenBounds screen) =>
        $"{screen.X}:{screen.Y}:{screen.Width}:{screen.Height}";
}
