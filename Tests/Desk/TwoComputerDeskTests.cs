using Hydra.Config;
using Hydra.Desk;
using Hydra.Display;
using Hydra.Relay;
using Hydra.Scenes;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Desk;

// Two desks talking to each other, which is where every mistake has actually been. Both regressions
// that reached the user — the controller restarting itself in a loop, and the peer never receiving
// the desk — were invisible to single-node tests and obvious here.
//
// The hardware is the real one: a MacBook with its built-in display plus an AORUS and a BenQ, and a
// Windows PC on the same AORUS and BenQ. Windows can read both monitors over DDC; the Mac's m1ddc
// reaches one of them and cannot name it.
public class TwoComputerDeskTests
{
    [Test]
    public async Task BothComputersEndUpWithTheSameThreeMonitors()
    {
        using var desk = await Desk.ConvergedAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(desk.Windows.Snapshot.Monitors, Has.Count.EqualTo(3));
            Assert.That(desk.Mac.Snapshot.Monitors, Has.Count.EqualTo(3),
                "the peer must receive the desk, not sit on 'waiting for the computers to report their screens'");
            Assert.That(
                desk.Mac.Snapshot.Monitors.Select(m => m.Label).OrderBy(l => l),
                Is.EqualTo(desk.Windows.Snapshot.Monitors.Select(m => m.Label).OrderBy(l => l)));
        }
    }

    [Test]
    public async Task ThePeerIsSeenEvenThoughItConnectedBeforeAnyoneWasListening()
    {
        // The relay announces its peers once, while this service is still being constructed. If the
        // peer list only ever came from that event, the controller would broadcast to nobody for
        // the rest of the session.
        using var desk = await Desk.ConvergedAsync(announcePeers: false);

        Assert.That(desk.Mac.Snapshot.Monitors, Is.Not.Empty);
    }

    [Test]
    public async Task TheDeskSettlesAndStopsRewritingItself()
    {
        using var desk = await Desk.ConvergedAsync();

        var settled = desk.Windows.ConfigWrites;
        for (var i = 0; i < 6; i++) await desk.PumpAsync();

        Assert.That(desk.Windows.ConfigWrites, Is.EqualTo(settled),
            "a desk that keeps rewriting its config restarts and pushes to peers forever");
    }

    [Test]
    public async Task TheControllerNeverRestartsItself()
    {
        using var desk = await Desk.ConvergedAsync();
        for (var i = 0; i < 6; i++) await desk.PumpAsync();

        Assert.That(desk.Windows.Restarts, Is.Zero,
            "restarting to apply its own desk change is what stopped the relay ever connecting");
    }

    [Test]
    public async Task ThePeerStopsRestartingOnceTheDeskHasSettled()
    {
        // The first desk to arrive legitimately changes this computer's crossings, and that does
        // need a restart. What must not happen is a second one, and a third, as the controller goes
        // on learning input codes and nudging monitors.
        using var desk = await Desk.ConvergedAsync();
        var afterFirstSync = desk.Mac.Restarts;

        for (var i = 0; i < 8; i++) await desk.PumpAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterFirstSync, Is.LessThanOrEqualTo(1), "one restart to adopt the desk, no more");
            Assert.That(desk.Mac.Restarts, Is.EqualTo(afterFirstSync),
                "a peer that restarts every time the desk changes never stays up long enough to be useful");
        }
    }

    [Test]
    public async Task EveryCrossingNamesRealScreensOnBothSides()
    {
        using var desk = await Desk.ConvergedAsync();

        var neighbours = desk.Windows.Config.Profiles.SelectMany(p => p.Hosts).SelectMany(h => h.Neighbours).ToList();
        Assert.That(neighbours, Is.Not.Empty, "the pointer needs somewhere to cross");

        // The identifiers have to be ones the computers actually use for their screens, or the
        // router matches nothing and the crossing silently does not exist.
        var known = desk.Windows.Snapshot.Monitors
            .SelectMany(m => m.Sources)
            .Select(s => s.Host)
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(neighbours.Select(n => n.SourceScreen), Is.All.Not.Null);
            Assert.That(neighbours.Select(n => n.DestScreen), Is.All.Not.Null);
            Assert.That(known, Is.Not.Empty);
        }
    }

    [Test]
    public async Task TheDeskArrangesItselfSoThePointerCanActuallyCross()
    {
        // A desk nobody has arranged by hand still has to work. Placing each computer's monitors
        // with a gap between them looked tidier and left the neighbour list empty, so the pointer
        // could never leave the computer it started on — with nothing anywhere saying why.
        using var desk = await Desk.ConvergedAsync();

        var neighbours = desk.Windows.Config.Profiles.SelectMany(p => p.Hosts).SelectMany(h => h.Neighbours).ToList();
        Assert.That(neighbours, Is.Not.Empty, "an automatically arranged desk must have somewhere to cross");
    }

    [Test]
    public async Task TheNamelessMacMonitorIsNotCalledNull()
    {
        using var desk = await Desk.ConvergedAsync();

        Assert.That(desk.Windows.Snapshot.Monitors.Select(m => m.Label), Has.None.Contains("null"));
    }

    // -- harness ---------------------------------------------------------------------------------

    // -- rearranging ------------------------------------------------------------------------------
    //
    // Moving a monitor did nothing until the agent was restarted. Saving rebuilt the crossings and
    // wrote them to the config, and then stopped; the router went on reading the layout it was
    // handed at startup, so the desk drawn on screen and the desk the pointer could feel disagreed.
    // Recompute could not cover it either — it applies the derived layout only when it differs from
    // the stored one, and saving has just made them the same.

    [Test]
    public async Task MovingAMonitorChangesTheCrossingsWithoutARestart()
    {
        using var desk = await Desk.ConvergedAsync();

        var before = Crossings(desk.Windows);

        // The built-in moved from the far left to between the two Windows screens.
        await desk.ArrangeAsync((desk.Aorus, 0, 0), (desk.BuiltIn, 2700, 300), (desk.Benq, 4200, 300));

        var after = Crossings(desk.Windows);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(after, Is.Not.EqualTo(before),
                "the router has to be handed the new layout the moment the desk changes");
            Assert.That(after, Does.Contain("Mac|Left|NINOG"));
            Assert.That(after, Does.Contain("Mac|Right|NINOG"),
                "a screen placed between two others has one of them on either side");
            Assert.That(desk.Windows.Restarts, Is.Zero, "nothing should need restarting to move a monitor");
        }
    }

    [Test]
    public async Task SwappingTwoMonitorsChangesWhichOneTheCrossingComesBackTo()
    {
        using var desk = await Desk.ConvergedAsync();

        await desk.ArrangeAsync((desk.Benq, 0, 0), (desk.Aorus, 2000, 0), (desk.BuiltIn, 4700, 0));
        var nextToAorus = Returns(desk.Windows);

        await desk.ArrangeAsync((desk.Aorus, 0, 0), (desk.Benq, 2700, 0), (desk.BuiltIn, 4700, 0));
        var nextToBenq = Returns(desk.Windows);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nextToAorus, Is.EqualTo(@"\\.\DISPLAY1"));
            Assert.That(nextToBenq, Is.EqualTo(@"\\.\DISPLAY2"),
                "swapping the two has to change which monitor the pointer comes back to, right away");
        }
    }

    [Test]
    public async Task ThePointerDoesNotJumpOverTheMonitorInBetween()
    {
        using var desk = await Desk.ConvergedAsync();

        await desk.ArrangeAsync((desk.Benq, 0, 0), (desk.Aorus, 1920, 0), (desk.BuiltIn, 4480, 0));

        var out_ = desk.Windows.Profile.Hosts.Single(h => h.Name == "NINOG").Neighbours;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(out_, Has.Count.EqualTo(1),
                "only the AORUS touches the Mac; the BenQ has the AORUS in front of it");
            Assert.That(out_[0].SourceScreen, Is.EqualTo(@"\\.\DISPLAY1"));
        }
    }

    [Test]
    public async Task TheOtherComputerIsRearrangedTooWithoutBeingRestarted()
    {
        // The follower has to end up with the same crossings, or the pointer goes one way and
        // cannot come back — which reads exactly like the arrangement being ignored.
        using var desk = await Desk.ConvergedAsync();

        await desk.ArrangeAsync((desk.Aorus, 0, 0), (desk.BuiltIn, 2700, 300), (desk.Benq, 4200, 300));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Crossings(desk.Mac), Is.EqualTo(Crossings(desk.Windows)));
            Assert.That(desk.Mac.Restarts, Is.Zero);
        }
    }

    private static List<string> Crossings(Node node) => node.Profile.Hosts
        .SelectMany(h => h.Neighbours.Select(n => $"{h.Name}|{n.Direction}|{n.Name}"))
        .Distinct()
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();

    // Where the pointer lands when it comes back off the Mac.
    private static string? Returns(Node node) =>
        node.Profile.Hosts.Single(h => h.Name == "Mac").Neighbours.Single().DestScreen;

    // -- switching a monitor ------------------------------------------------------------------------

    [Test]
    public async Task TheComputerBeingSwitchedToIsWokenFirst()
    {
        // A monitor asked for an input nothing is driving finds no signal and goes hunting for one,
        // which lands it back on the computer it just left -- the switch goes black and undoes
        // itself a few seconds later. It works whenever that computer happens to be awake already,
        // which is what makes the missing step look like an intermittent fault instead.
        using var desk = await Desk.ConvergedAsync();
        var aorus = await desk.SwitchableAorusAsync();

        await desk.SwitchAsync(aorus, "Mac");

        var woken = desk.Mac.Router.Commands.IndexOf("wake");
        Assert.That(woken, Is.GreaterThanOrEqualTo(0),
            "the computer the monitor is being handed to has to be driving that output before the switch");
    }

    [Test]
    public async Task TheCrossingsFollowTheMonitorTheMomentItChangesHands()
    {
        // The monitor now belongs to someone else, so the pointer crosses somewhere else. Leaving
        // that to the next round left the crossings pointing at the computer the monitor used to
        // belong to, and the only way through was to keep moving the mouse until they caught up.
        using var desk = await Desk.ConvergedAsync();
        var aorus = await desk.SwitchableAorusAsync();

        var before = desk.Windows.Profile.Hosts
            .SelectMany(h => h.Neighbours.Select(n => $"{h.Name}|{n.Direction}|{n.DestScreen}"))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        await desk.SwitchAsync(aorus, "Mac");

        var after = desk.Windows.Profile.Hosts
            .SelectMany(h => h.Neighbours.Select(n => $"{h.Name}|{n.Direction}|{n.DestScreen}"))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Is.Not.Empty, "pre-condition: the pointer could cross to begin with");
            Assert.That(after, Is.Not.EqualTo(before),
                "handing a monitor to the other computer changes where the pointer crosses, and the "
                + "router has to be told before the user reaches the edge");
            Assert.That(desk.Windows.Restarts, Is.Zero);
        }
    }

    private sealed class Desk : IDisposable
    {
        public required Node Windows { get; init; }
        public required Node Mac { get; init; }
        public required Wire Wire { get; init; }

        // The three monitors by the only thing that tells them apart here: their size.
        public string Aorus => MonitorOfWidth(2560);
        public string Benq => MonitorOfWidth(1920);
        public string BuiltIn => MonitorOfWidth(1352);

        private string MonitorOfWidth(int width) =>
            Windows.Snapshot.Monitors.Single(m => m.Width == width).Id;


        // The AORUS with both sockets known, which is what makes it switchable at all. The Mac
        // cannot read that monitor while Windows is on it, so in real use the code is either learned
        // the one time the Mac is on screen or entered by hand; either way it ends up here.
        public async Task<string> SwitchableAorusAsync()
        {
            var aorus = Windows.Snapshot.Monitors.Single(m => m.Width == 2560);
            await Windows.Service.ProbeInputAsync(aorus.Id, "Mac", 17);
            await Wire.DrainAsync();
            return aorus.Id;
        }

        // Drives the wire while the switch is in flight, so the wake request reaches the other
        // computer and its answer comes back — which is what happens by itself on a real relay.
        public async Task SwitchAsync(string monitorId, string host)
        {
            var switching = Windows.Service.SetMonitorHostAsync(monitorId, host);
            for (var i = 0; i < 200 && !switching.IsCompleted; i++)
            {
                await Wire.DrainAsync();
                await Task.Delay(10);
            }
            await switching;
        }
        public async Task ArrangeAsync(params (string Id, int X, int Y)[] places)
        {
            var byId = Windows.Snapshot.Monitors.ToDictionary(m => m.Id);
            await Windows.Service.SaveArrangementAsync(places
                .Select(p => new DeskPlacement(p.Id, p.X, p.Y, byId[p.Id].Width, byId[p.Id].Height))
                .ToList());
            await Wire.DrainAsync();
            await PumpAsync();
        }

        public static async Task<Desk> ConvergedAsync(bool announcePeers = true)
        {
            var wire = new Wire();
            var windows = Node.Create("NINOG", Mode.Master, wire, WindowsScreens(), WindowsMonitors());
            var mac = Node.Create("Mac", Mode.Slave, wire, MacScreens(), MacMonitors());
            wire.Connect(windows, mac);

            // The real relay records who is on the desk as it connects them, whether or not anything
            // was listening to the announcement at the time.
            await windows.World.SetPeerScreens("Mac", []);
            await mac.World.AddMaster("NINOG", new MasterConfigMessage(null));

            if (announcePeers)
            {
                await windows.Relay.FirePeersChanged("Mac");
                await mac.Relay.FirePeersChanged("NINOG");
            }

            var desk = new Desk { Windows = windows, Mac = mac, Wire = wire };
            await wire.DrainAsync();
            for (var i = 0; i < 6; i++) await desk.PumpAsync();
            return desk;
        }

        public async Task PumpAsync()
        {
            await Mac.Service.PumpAsync();
            await Wire.DrainAsync();
            await Windows.Service.PumpAsync();
            await Wire.DrainAsync();
        }

        public void Dispose()
        {
            Windows.Dispose();
            Mac.Dispose();
        }

        private static LocalScreenSnapshot WindowsScreens() => Snapshot(
            ("NINOG:0", @"\\.\DISPLAY1", null, 0, 0, 2560, 1440),
            ("NINOG:1", @"\\.\DISPLAY2", null, 2560, 0, 1920, 1080));

        private static LocalScreenSnapshot MacScreens() => Snapshot(
            ("Mac:0", null, "Built-in Retina Display", 0, 0, 1352, 878),
            ("Mac:1", null, "AORUS FI27Q-X", 1352, 0, 1920, 1080),
            ("Mac:2", null, "BenQ XL2420T", 3272, 0, 1920, 1080));

        private static List<PhysicalMonitorInfo> WindowsMonitors() =>
        [
            new(@"\\.\DISPLAY1", "AORUS", @"\\.\DISPLAY1", 15, [1, 3, 15, 17], ["AORUS", "Generic PnP Monitor"]),
            new(@"\\.\DISPLAY2", "XL2410T", @"\\.\DISPLAY2", 15, [1, 3, 17, 18, 15], ["XL2410T", "BenQ XL2420T (DisplayPort)"]),
        ];

        // m1ddc reaches one display and cannot name it: "[1] (null) (37D8832A-…)".
        private static List<PhysicalMonitorInfo> MacMonitors() =>
        [
            new("37D8832A-2D66-02CA-B9F7-8F30A301B230", "Display 1", null, null, null,
                ["37D8832A-2D66-02CA-B9F7-8F30A301B230"]),
        ];

        private static LocalScreenSnapshot Snapshot(params (string Name, string? Output, string? Display, int X, int Y, int W, int H)[] screens) =>
            new(
                screens.Select(s => new ScreenRect(s.Name, s.Name.Split(':')[0], s.X, s.Y, s.W, s.H, IsLocal: true,
                    new ScreenIdentity { ScreenName = s.Name, Output = s.Output, DisplayName = s.Display })).ToList(),
                screens.Select(s => new ScreenInfoEntry(s.Name, s.X, s.Y, s.W, s.H, 1.0m)).ToList());
    }

    private sealed class Node : IDisposable
    {
        public required string Name { get; init; }
        public DeskService Service { get; set; } = null!;
        public required WiredRelay Relay { get; init; }
        public required DeskConfigStore Store { get; init; }
        public required WorldState World { get; init; }
        public required string Directory { get; init; }
        public required IHydraProfile Profile { get; init; }
        public required StubRouter Router { get; init; }
        public int Restarts;

        public DeskSnapshot Snapshot => Service.Snapshot;
        public HydraConfigFile Config => Store.Load();
        public int ConfigWrites => File.Exists(Store.Path) ? File.ReadAllText(Store.Path).GetHashCode() : 0;

        public static Node Create(string name, Mode mode, Wire wire, LocalScreenSnapshot screens, List<PhysicalMonitorInfo> monitors)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"screenfuse-desk-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var configPath = Path.Combine(directory, "screenfuse.conf");

            var profile = new HydraProfile(
                new HydraConfigFile { Name = name },
                new HydraConfig
                {
                    Mode = mode,
                    ProfileName = "Default",
                    Controller = "NINOG",
                    Hosts = [new HostConfig { Name = "NINOG" }, new HostConfig { Name = "Mac" }],
                    EmbeddedStyxServer = mode == Mode.Master
                        ? new EmbeddedStyxServerConfig { Port = 5000, Password = "a-password-long-enough", DiscoveryName = "test" }
                        : null,
                    EmbeddedStyx = mode == Mode.Slave
                        ? new EmbeddedStyxConfig { Server = "auto://test", Password = "a-password-long-enough" }
                        : null,
                });

            var router = new StubRouter(monitors);
            var relay = new WiredRelay(name, wire);
            var world = new WorldState();
            var store = new DeskConfigStore(configPath);
            File.WriteAllText(configPath, store.Serialize(Seed(name, mode)));

            var node = new Node { Name = name, Relay = relay, Store = store, Directory = directory, World = world, Profile = profile, Router = router };
            node.Service = new DeskService(
                profile,
                new FakeScreenDetector { Snapshot = screens },
                router,
                relay,
                world,
                store,
                new ControllerOverrideStore(configPath),
                new StubScenes(),
                NullLogger<DeskService>.Instance,
                () => Interlocked.Increment(ref node.Restarts),
                TimeSpan.Zero);
            return node;
        }

        private static HydraConfigFile Seed(string name, Mode mode) => new()
        {
            Name = name,
            Profiles =
            [
                new HydraConfig
                {
                    Mode = mode,
                    ProfileName = "Default",
                    Controller = "NINOG",
                    Hosts = [new HostConfig { Name = "NINOG" }, new HostConfig { Name = "Mac" }],
                    EmbeddedStyxServer = mode == Mode.Master
                        ? new EmbeddedStyxServerConfig { Port = 5000, Password = "a-password-long-enough", DiscoveryName = "test" }
                        : null,
                    EmbeddedStyx = mode == Mode.Slave
                        ? new EmbeddedStyxConfig { Server = "auto://test", Password = "a-password-long-enough" }
                        : null,
                },
            ],
        };

        public void Dispose()
        {
            try { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
            catch (IOException) { /* the test is over either way */ }
        }
    }

    // Carries messages from one desk to the other, the way the relay does.
    private sealed class Wire
    {
        private readonly Dictionary<string, WiredRelay> _nodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<(string From, string[] Targets, byte[] Payload)> _inFlight = new();

        public void Connect(Node a, Node b)
        {
            _nodes[a.Name] = a.Relay;
            _nodes[b.Name] = b.Relay;
        }

        // IRelaySender.Send cannot be awaited, so deliveries are queued and drained at a known
        // point instead of being fired into a discarded task. A test whose messages arrive whenever
        // the scheduler feels like it passes and fails for reasons that have nothing to do with the
        // code under test — which is exactly what happened, locally green and red on CI.
        public void Queue(string from, string[] targets, byte[] payload) =>
            _inFlight.Enqueue((from, targets, payload));

        public async Task DrainAsync()
        {
            // Delivering a message can queue more, so keep going until the desk falls quiet.
            for (var guard = 0; _inFlight.Count > 0 && guard < 200; guard++)
            {
                var (from, targets, payload) = _inFlight.Dequeue();
                foreach (var target in targets)
                {
                    if (!_nodes.TryGetValue(target, out var relay)) continue;
                    var decoded = MessageSerializer.Decode(payload);
                    await relay.ReceiveAsync(from, decoded.Kind, decoded.Bytes);
                }
            }
        }
    }

    private sealed class WiredRelay(string name, Wire wire) : IRelaySender
    {
        public bool IsConnected => true;
        public event Func<string[], Task>? PeersChanged;
        public event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived;
#pragma warning disable CS0067 // the desk does not use it; the interface requires it
        public event Func<Task>? Disconnected;
#pragma warning restore CS0067

        public void Send(string[] targetHosts, byte[] payload) => wire.Queue(name, targetHosts, payload);

        public async Task ReceiveAsync(string from, MessageKind kind, ReadOnlyMemory<byte> body)
        {
            if (MessageReceived != null) await MessageReceived(from, kind, body);
        }

        public async Task FirePeersChanged(params string[] hosts)
        {
            if (PeersChanged != null) await PeersChanged(hosts);
        }
    }

    private sealed class StubRouter(List<PhysicalMonitorInfo> monitors) : IDisplayRouter
    {
        // What was asked of the hardware, in order, so a test can say "woken, then switched".
        public List<string> Commands { get; } = [];

        public Task<IReadOnlyList<DisplayCommandResult>> ApplyAsync(DisplayRoutingConfig routing, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DisplayCommandResult>>([]);
        public Task<IReadOnlyList<DisplayCommandResult>> DoctorAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DisplayCommandResult>>([]);
        public Task<IReadOnlyList<PhysicalMonitorInfo>> InventoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PhysicalMonitorInfo>>(monitors);
        public Task<DisplayCommandResult> SetInputAsync(string id, int input, CancellationToken cancellationToken = default)
        {
            Commands.Add($"input {id}={input}");
            return Task.FromResult(new DisplayCommandResult($"set {id} input {input}", true));
        }
        public Task<DisplayCommandResult> SetDisplayPowerAsync(bool wake, CancellationToken cancellationToken = default)
        {
            Commands.Add(wake ? "wake" : "sleep");
            return Task.FromResult(new DisplayCommandResult("power", true));
        }
    }

    private sealed class StubScenes : ISceneCoordinator
    {
        public string? CurrentScene => "Default";
        public IReadOnlyList<string> AvailableScenes => ["Default"];
        public IReadOnlyList<string> ConnectedPeers => [];
        public IReadOnlyList<string> ExpectedPeers => [];
        public Task<SceneActivationResult> ActivateAsync(string scene, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SceneActivationResult(true, scene, "ok"));
    }
}
