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
        Direction remoteDirection = Direction.Right)
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
}
