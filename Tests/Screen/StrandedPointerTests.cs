using Hydra.Config;
using Hydra.Screen;
using Tests.Setup;

namespace Tests.Screen;

// The pointer went to the other computer and the cursor never came back.
//
// When the pointer leaves, this computer hides its own cursor — there is nothing here to point
// with. Getting home depends entirely on the layout, so if the desk changes while the pointer is
// away and the new layout gives that screen no edges, it is stranded: the pointer is on a machine
// it cannot leave, and on this one there is no cursor at all. No amount of moving the mouse
// recovers it, because every input is being forwarded to a screen with no way off it.
//
// It is reachable in ordinary use — a monitor losing its owner is enough to empty the crossings —
// and the symptom is simply "my cursor is gone", which points nowhere near the cause.
public class StrandedPointerTests
{
    [Test]
    public async Task ThePointerComesHomeWhenTheDeskLeavesItNowhereToGo()
    {
        var profile = TransitionTestHelper.Profile("home", new HydraConfig { Mode = Mode.Master, Hosts = Across() });
        var (platform, relay, service) = TransitionTestHelper.CreateServiceWith(profile);
        try
        {
            await service.StartAsync(CancellationToken.None);
            await TransitionTestHelper.BringRemoteOnline(relay);

            platform.FireMouseMove(2559, 720);
            Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition: the pointer is away");

            // The desk loses its crossings — a monitor whose owner became uncertain is enough.
            profile.ApplyHosts([new HostConfig { Name = "home" }, new HostConfig { Name = "remote" }]);
            await service.FlushAsync();

            Assert.That(platform.IsOnVirtualScreen, Is.False,
                "with no way back, the pointer has to be brought home rather than left out there");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await platform.DisposeAsync();
        }
    }

    [Test]
    public async Task TheCursorIsGivenBackWhenThePointerIsRecovered()
    {
        // The half that actually bites: a pointer quietly restored while the cursor stays hidden is
        // no better than a stranded one.
        var profile = TransitionTestHelper.Profile("home", new HydraConfig { Mode = Mode.Master, Hosts = Across() });
        var (platform, relay, service) = TransitionTestHelper.CreateServiceWith(profile);
        try
        {
            await service.StartAsync(CancellationToken.None);
            await TransitionTestHelper.BringRemoteOnline(relay);

            platform.FireMouseMove(2559, 720);
            Assert.That(platform.HideCursorCalled, Is.True, "pre-condition: nothing to point with while the pointer is away");
            platform.ShowCursorCalled = false;

            profile.ApplyHosts([new HostConfig { Name = "home" }, new HostConfig { Name = "remote" }]);
            await service.FlushAsync();

            Assert.That(platform.ShowCursorCalled, Is.True, "the cursor comes back with the pointer");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await platform.DisposeAsync();
        }
    }

    [Test]
    public async Task APointerWithAWayBackIsLeftWhereItIs()
    {
        // The guard must not drag the pointer home every time the desk is touched. A rearrangement
        // that keeps a way back changes nothing about where the pointer is.
        var profile = TransitionTestHelper.Profile("home", new HydraConfig { Mode = Mode.Master, Hosts = Across() });
        var (platform, relay, service) = TransitionTestHelper.CreateServiceWith(profile);
        try
        {
            await service.StartAsync(CancellationToken.None);
            await TransitionTestHelper.BringRemoteOnline(relay);

            platform.FireMouseMove(2559, 720);
            Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition");

            // Same crossings, rebuilt — the desk was saved, nothing moved.
            profile.ApplyHosts(Across());
            await service.FlushAsync();

            Assert.That(platform.IsOnVirtualScreen, Is.True,
                "there is still a way back, so the pointer stays where the user put it");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await platform.DisposeAsync();
        }
    }

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
