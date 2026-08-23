using Hydra.Config;
using Hydra.Screen;
using Tests.Setup;

namespace Tests.Screen;

// Rearranging the desk changed nothing until something else happened to rebuild the layout.
//
// The pointer crosses using the layout the router derived from Hosts, not the config on disk. The
// router built that layout at startup and rebuilt it only when the screens changed or a peer came
// and went — and dragging a monitor around the desk does neither. So the desk was rewritten, both
// computers agreed on the new crossings, every test on the derivation passed, and the pointer went
// on using the arrangement it had been started with.
//
// It looked intermittent, which is the worst part: reconnecting a peer rebuilt the layout by
// accident, so an arrangement would sometimes "take" minutes later and sometimes never.
public class LayoutFollowsTheDeskTests
{
    [Test]
    public async Task ANewCrossingWorksWithoutRestartingAnything()
    {
        var profile = Nowhere();
        var (platform, relay, service) = TransitionTestHelper.CreateServiceWith(profile);
        try
        {
            await service.StartAsync(CancellationToken.None);
            await TransitionTestHelper.BringRemoteOnline(relay);

            platform.FireMouseMove(2559, 720);
            Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: nowhere to go yet");

            profile.ApplyHosts(Across());
            await service.FlushAsync();

            platform.FireMouseMove(2559, 720);
            Assert.That(platform.IsOnVirtualScreen, Is.True,
                "the pointer uses the layout, so the layout has to be rebuilt when the desk changes");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await platform.DisposeAsync();
        }
    }

    [Test]
    public async Task RemovingACrossingStopsThePointerLeaving()
    {
        // The other way round matters just as much: a monitor dragged out from beside another must
        // stop taking the pointer, or the desk on screen and the desk you can feel disagree again.
        var profile = TransitionTestHelper.Profile("home", new HydraConfig { Mode = Mode.Master, Hosts = Across() });
        var (platform, relay, service) = TransitionTestHelper.CreateServiceWith(profile);
        try
        {
            await service.StartAsync(CancellationToken.None);
            await TransitionTestHelper.BringRemoteOnline(relay);

            platform.FireMouseMove(2559, 720);
            Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition: it crosses to begin with");

            // And comes back, which leaves the pointer where the rest of the test needs it.
            platform.FireMouseDelta(-20, 0);
            Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: and it comes back");

            profile.ApplyHosts(NoCrossings());
            await service.FlushAsync();

            platform.FireMouseMove(2559, 720);

            Assert.That(platform.IsOnVirtualScreen, Is.False,
                "the crossing was taken away, so the edge is just an edge again");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await platform.DisposeAsync();
        }
    }

    private static IHydraProfile Nowhere() =>
        TransitionTestHelper.Profile("home", new HydraConfig { Mode = Mode.Master, Hosts = NoCrossings() });

    private static List<HostConfig> NoCrossings() =>
        [new HostConfig { Name = "home" }, new HostConfig { Name = "remote" }];

    private static List<HostConfig> Across() =>
    [
        new HostConfig
        {
            Name = "home",
            Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "remote" }],
        },
        new HostConfig
        {
            Name = "remote",
            Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "home" }],
        },
    ];
}
