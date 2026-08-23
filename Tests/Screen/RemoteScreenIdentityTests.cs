using Hydra.Config;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Screen;

// A crossing names the screen it arrives on, and that name belongs to the computer that owns it —
// "Built-in Retina Display", not an index. The computer holding the keyboard builds its picture of
// a remote screen from what that computer sent, so if the identifiers do not travel, the destination
// resolves to nothing and the crossing is dropped in silence. The desk then reports crossings on
// both machines, agrees with itself, and the pointer still cannot leave the computer it is on.
public class RemoteScreenIdentityTests
{
    [Test]
    public void AScreenIdentifiesItselfByEveryNameItsOwnerUses()
    {
        var entry = new ScreenInfoEntry("Mac:1", 0, 0, 1920, 1080, 1.0m, null,
            Output: null, DisplayName: "Built-in Retina Display", PlatformId: "1");

        var remote = new ScreenRect(entry.Name, "Mac", entry.X, entry.Y, entry.Width, entry.Height, IsLocal: false,
            new ScreenIdentity
            {
                ScreenName = entry.Name,
                Output = entry.Output,
                DisplayName = entry.DisplayName,
                PlatformId = entry.PlatformId,
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(remote.Identity!.Matches("Built-in Retina Display"), Is.True);
            Assert.That(remote.Identity!.Matches("Mac:1"), Is.True);
            Assert.That(remote.Identity!.Matches("Some Other Monitor"), Is.False);
        }
    }

    [Test]
    public void ACrossingToARemoteScreenNamedByItsOwnerIsFound()
    {
        // The real shape: Windows leaves \\.\DISPLAY1 rightwards for the Mac's built-in display.
        var local = new ScreenRect("NINOG:0", "NINOG", 0, 0, 2560, 1440, IsLocal: true,
            new ScreenIdentity { ScreenName = "NINOG:0", Output = @"\\.\DISPLAY1" });
        var remote = new ScreenRect("Mac", "Mac", 0, 0, 1352, 878, IsLocal: false,
            new ScreenIdentity { ScreenName = "Mac", DisplayName = "Built-in Retina Display", PlatformId = "1" });

        var hosts = new List<HostConfig>
        {
            new()
            {
                Name = "NINOG",
                Neighbours =
                [
                    new NeighbourConfig
                    {
                        Direction = Direction.Right,
                        Name = "Mac",
                        SourceScreen = @"\\.\DISPLAY1",
                        DestScreen = "Built-in Retina Display",
                        Mirror = false,
                    },
                ],
            },
            new() { Name = "Mac" },
        };

        var layout = new ScreenLayout([local, remote], hosts, null, [], NullLogger.Instance);
        var hit = layout.DetectEdgeExit(local, local.Width - 1, local.Height / 2);

        Assert.That(hit, Is.Not.Null, "the pointer must find the screen the crossing names");
        Assert.That(hit!.Destination.Host, Is.EqualTo("Mac"));
    }

    [Test]
    public void ACrossingToANameNobodyKnowsFindsNothing()
    {
        // Proves the test above is not passing by accident: an identifier the remote computer does
        // not use for any of its screens matches nothing, which is exactly the silent failure that
        // stopped the pointer for a whole day.
        var local = new ScreenRect("NINOG:0", "NINOG", 0, 0, 2560, 1440, IsLocal: true,
            new ScreenIdentity { ScreenName = "NINOG:0", Output = @"\\.\DISPLAY1" });
        var remote = new ScreenRect("Mac", "Mac", 0, 0, 1352, 878, IsLocal: false,
            new ScreenIdentity { ScreenName = "Mac", DisplayName = "Built-in Retina Display" });

        var hosts = new List<HostConfig>
        {
            new()
            {
                Name = "NINOG",
                Neighbours =
                [
                    new NeighbourConfig
                    {
                        Direction = Direction.Right,
                        Name = "Mac",
                        SourceScreen = @"\\.\DISPLAY1",
                        DestScreen = "A Monitor That Is Not There",
                        Mirror = false,
                    },
                ],
            },
            new() { Name = "Mac" },
        };

        var layout = new ScreenLayout([local, remote], hosts, null, [], NullLogger.Instance);

        Assert.That(layout.DetectEdgeExit(local, local.Width - 1, local.Height / 2), Is.Null);
    }
}
