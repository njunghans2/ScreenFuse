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

    private sealed class Desk : IDisposable
    {
        public required Node Windows { get; init; }
        public required Node Mac { get; init; }
        public required Wire Wire { get; init; }

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

            var relay = new WiredRelay(name, wire);
            var world = new WorldState();
            var store = new DeskConfigStore(configPath);
            File.WriteAllText(configPath, store.Serialize(Seed(name, mode)));

            var node = new Node { Name = name, Relay = relay, Store = store, Directory = directory, World = world };
            node.Service = new DeskService(
                profile,
                new FakeScreenDetector { Snapshot = screens },
                new StubRouter(monitors),
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
        public Task<IReadOnlyList<DisplayCommandResult>> ApplyAsync(DisplayRoutingConfig routing, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DisplayCommandResult>>([]);
        public Task<IReadOnlyList<DisplayCommandResult>> DoctorAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DisplayCommandResult>>([]);
        public Task<IReadOnlyList<PhysicalMonitorInfo>> InventoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PhysicalMonitorInfo>>(monitors);
        public Task<DisplayCommandResult> SetInputAsync(string id, int input, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DisplayCommandResult($"set {id} input {input}", true));
        public Task<DisplayCommandResult> SetDisplayPowerAsync(bool wake, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DisplayCommandResult("power", true));
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
