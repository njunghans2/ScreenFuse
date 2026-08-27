using Hydra.Config;
using Hydra.Relay;
using Hydra.Screen;
using Tests.Setup;

namespace Tests.Screen;

// A monitor changes hands and the machine it left goes deaf.
//
// Handing a monitor to the other computer does two things here at once: the desk rewrites the
// crossings, and Windows notices the display is gone and says so. The first goes through the shared
// rebuild, which knows how to bring a stranded pointer home. The second used to build the layout by
// itself — so it alone skipped that check, and it is usually the one that writes last. The
// pointer ends up on a screen with no way off it, this computer's cursor is hidden because the
// pointer is supposed to be elsewhere, and every key and click is being swallowed on its behalf.
//
// The second half is quieter and bites more often: the park point is the middle of a local screen,
// and it is where the cursor is physically pinned while the pointer is away. If that screen is the
// one that just left, the park point is off the desktop. The cursor gets clamped somewhere else,
// every movement is then measured against an anchor it is not sitting on, comes out as an
// impossible jump, and is thrown away as bogus — the mouse stops working while still being eaten.
public class DisplayChangeStrandsPointerTests
{
    [Test]
    public async Task ThePointerComesHomeWhenTheDisplayItCrossedFromIsTakenAway()
    {
        var screens = TwoLocalScreens();
        var profile = TransitionTestHelper.Profile("home", new HydraConfig { Mode = Mode.Master, Hosts = ThroughTheSecondScreen() });
        var (platform, relay, service) = TransitionTestHelper.CreateServiceWith(profile, screens);
        try
        {
            await service.StartAsync(CancellationToken.None);
            await TransitionTestHelper.BringRemoteOnline(relay);

            platform.FireMouseMove(4479, 540);   // right edge of the second screen
            Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition: the pointer is away");

            // The desk hands that monitor to the other computer, so this one loses the display.
            screens.Snapshot = OnlyTheFirstScreen();
            await screens.FireChange();
            await service.FlushAsync();

            Assert.That(platform.IsOnVirtualScreen, Is.False,
                "the screen the pointer crossed from is gone, so there is no way back — it has to be "
                + "brought home rather than left out there with local input still being swallowed");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await platform.DisposeAsync();
        }
    }

    [Test]
    public async Task TheParkPointMovesOntoADisplayThatStillExists()
    {
        var screens = TwoLocalScreens();
        // Crossings that survive losing the second screen: the pointer is not stranded here, so the
        // only thing that can go wrong is where it is being parked.
        var profile = TransitionTestHelper.Profile("home", new HydraConfig { Mode = Mode.Master, Hosts = FromWhicheverScreenIsOnTheRight() });
        var (platform, relay, service) = TransitionTestHelper.CreateServiceWith(profile, screens);
        platform.Desktop = BothScreens;
        try
        {
            await service.StartAsync(CancellationToken.None);
            await TransitionTestHelper.BringRemoteOnline(relay);

            platform.FireMouseMove(4479, 540);
            Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition: the pointer is away");
            Assert.That(platform.WarpX, Is.EqualTo(3520), "pre-condition: parked on the second screen");

            screens.Snapshot = OnlyTheFirstScreen();
            platform.Desktop = FirstScreenOnly;
            await screens.FireChange();
            await service.FlushAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(platform.IsOnVirtualScreen, Is.True, "there is still a way back, so the pointer stays");
                Assert.That(platform.WarpX, Is.EqualTo(1280),
                    "the cursor cannot be pinned to the middle of a display that no longer exists");
                Assert.That(platform.WarpY, Is.EqualTo(720));
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await platform.DisposeAsync();
        }
    }

    [Test]
    public async Task TheMouseStillWorksAfterTheDisplayItWasParkedOnIsTakenAway()
    {
        // The point of the park point moving: movement is measured as a delta from it, so an anchor
        // the cursor is not sitting on turns every real movement into an impossible jump.
        var screens = TwoLocalScreens();
        var profile = TransitionTestHelper.Profile("home", new HydraConfig { Mode = Mode.Master, Hosts = FromWhicheverScreenIsOnTheRight() });
        var (platform, relay, service) = TransitionTestHelper.CreateServiceWith(profile, screens);
        platform.Desktop = BothScreens;
        try
        {
            await service.StartAsync(CancellationToken.None);
            await TransitionTestHelper.BringRemoteOnline(relay);
            platform.FireMouseMove(4479, 540);

            screens.Snapshot = OnlyTheFirstScreen();
            platform.Desktop = FirstScreenOnly;
            await screens.FireChange();
            await service.FlushAsync();

            // Wherever the cursor actually ended up, a small nudge from there is a small movement.
            relay.Sent.Clear();
            platform.FireMouseMove(platform.WarpX + 5, platform.WarpY + 3);

            Assert.That(relay.Sent.Any(m => m.Kind == MessageKind.MouseMove), Is.True,
                "the cursor is pinned wherever the desktop allowed, so a movement measured against a "
                + "park point it is not sitting on comes out as an impossible jump and is discarded "
                + "as bogus -- the mouse stops working while every event is still being swallowed");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await platform.DisposeAsync();
        }
    }

    private static readonly (int, int, int, int) BothScreens = (0, 0, 4480, 1440);
    private static readonly (int, int, int, int) FirstScreenOnly = (0, 0, 2560, 1440);

    private static FakeScreenDetector TwoLocalScreens() => new()
    {
        Snapshot = new LocalScreenSnapshot(
        [
            new ScreenRect("home:0", "home", 0, 0, 2560, 1440, IsLocal: true),
            new ScreenRect("home:1", "home", 2560, 0, 1920, 1080, IsLocal: true),
        ],
        [
            new ScreenInfoEntry("home:0", 0, 0, 2560, 1440, 1.0m),
            new ScreenInfoEntry("home:1", 2560, 0, 1920, 1080, 1.0m),
        ]),
    };

    private static LocalScreenSnapshot OnlyTheFirstScreen() => new(
        [new ScreenRect("home:0", "home", 0, 0, 2560, 1440, IsLocal: true)],
        [new ScreenInfoEntry("home:0", 0, 0, 2560, 1440, 1.0m)]);

    // The way back names the second screen, so losing it leaves the remote screen with no edges.
    private static List<HostConfig> ThroughTheSecondScreen() =>
    [
        new HostConfig
        {
            Name = "home",
            Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "remote", SourceScreen = "home:1" }],
        },
        new HostConfig
        {
            Name = "remote",
            Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "home", DestScreen = "home:1" }],
        },
    ];

    // The way back names no screen, so it simply follows whichever one is left.
    private static List<HostConfig> FromWhicheverScreenIsOnTheRight() =>
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
