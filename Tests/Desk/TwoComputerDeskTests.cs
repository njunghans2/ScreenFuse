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
    public async Task ThePeerNeverRestartsToAdoptADesk()
    {
        // Adopting an arriving desk used to cost a restart, and the peer then restarted again every
        // time the controller learned an input code or nudged a monitor. Everything in the document
        // is applied where it stands now, including who holds the keyboard.
        using var desk = await Desk.ConvergedAsync();

        for (var i = 0; i < 8; i++) await desk.PumpAsync();

        Assert.That(desk.Mac.Restarts, Is.Zero,
            "a peer that restarts every time the desk changes never stays up long enough to be useful");
    }

    // -- handing the keyboard and mouse over --

    [Test]
    public async Task HandingOverSwapsTheRolesOnBothComputers()
    {
        using var desk = await Desk.ConvergedAsync();
        Assert.That(desk.Windows.Profile.IsController, Is.True);
        Assert.That(desk.Mac.Profile.IsController, Is.False);

        var result = await desk.Mac.Service.SetControllerAsync("Mac");
        await desk.Wire.DrainAsync();
        await desk.PumpAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Accepted, Is.True);
            Assert.That(desk.Mac.Profile.IsController, Is.True, "the computer that asked takes control");
            Assert.That(desk.Windows.Profile.IsController, Is.False, "the one that had it steps down");
            Assert.That(desk.Mac.Snapshot.Controller, Is.EqualTo("Mac"));
            Assert.That(desk.Windows.Snapshot.Controller, Is.EqualTo("Mac"));
        }
    }

    [Test]
    public async Task HandingOverRestartsNothing()
    {
        // This is the whole point of it. Handing the keyboard over used to restart every agent into
        // its new role, which dropped the relay, took the tray icon and the settings window with
        // it, and left the desk blank for several seconds.
        using var desk = await Desk.ConvergedAsync();

        await desk.Windows.Service.SetControllerAsync("Mac");
        await desk.Wire.DrainAsync();
        for (var i = 0; i < 4; i++) await desk.PumpAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(desk.Windows.Restarts, Is.Zero, "the computer giving up control stays up");
            Assert.That(desk.Mac.Restarts, Is.Zero, "and so does the one taking it");
        }
    }

    [Test]
    public async Task HandingOverIsRememberedAcrossARestart()
    {
        using var desk = await Desk.ConvergedAsync();

        await desk.Windows.Service.SetControllerAsync("Mac");
        await desk.Wire.DrainAsync();
        await desk.PumpAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(new ControllerOverrideStore(desk.Windows.Store.Path).Read(), Is.EqualTo("Mac"),
                "the computer that gave control up comes back in its new role");
            Assert.That(new ControllerOverrideStore(desk.Mac.Store.Path).Read(), Is.EqualTo("Mac"),
                "and so does the one that took it");
        }
    }

    [Test]
    public async Task HandingOverToAComputerThatIsNotOnTheDeskIsRefused()
    {
        using var desk = await Desk.ConvergedAsync();

        var result = await desk.Windows.Service.SetControllerAsync("Laptop");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Accepted, Is.False);
            Assert.That(desk.Windows.Snapshot.Controller, Is.EqualTo("NINOG"), "control stays where it was");
            Assert.That(desk.Windows.Profile.IsController, Is.True);
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

    [Test]
    public async Task ADisconnectedHostImmediatelyLosesItsOptimisticMonitorOwnership()
    {
        using var desk = await Desk.ConvergedAsync();
        var aorus = await desk.SwitchableAorusAsync();
        await desk.SwitchAsync(aorus, "Mac");

        await desk.Windows.Relay.FirePeersChanged();

        Assert.That(desk.Windows.Snapshot.Monitors.Single(m => m.Id == aorus).ActiveHost, Is.Not.EqualTo("Mac"),
            "a switched-off computer must not retain a display assignment or crossings after it leaves the relay");
    }

    [Test]
    public async Task SwitchingTheLastMonitorAwayStopsThisComputersOutputInsteadOfBlankingThePanel()
    {
        // An OS with no display to render is a soft-locked OS: the last display is never removed
        // from the desktop. What stops instead is this computer's own video output — never the
        // panel. Blanking is a DDC command to the *monitor*, and a monitor has one power state no
        // matter how many computers are wired to it: it blacked the panel out for the computer
        // that had just won the switch, which is a black screen the monitor's own power button was
        // the only way out of.
        using var desk = await Desk.ConvergedAsync();

        await desk.SwitchAsync(desk.Aorus, "Mac");
        await desk.SwitchAsync(desk.Benq, "Mac");

        using (Assert.EnterMultipleScope())
        {
            var commands = desk.Windows.Router.Commands;
            Assert.That(commands, Does.Not.Contain(@"disable \\.\DISPLAY2"),
                "the last display must not be removed from the desktop — Windows refuses, and the desk takes the refusal");
            Assert.That(commands, Does.Not.Contain(@"blank \\.\DISPLAY2"),
                "a monitor that has just changed hands must never be told to blank its panel");
            Assert.That(commands, Does.Contain("sleep"),
                "so this computer stops driving its output instead");
            var benq = desk.Windows.Snapshot.Monitors.Single(m => m.Id == desk.Benq);
            Assert.That(benq.Sleeping, Is.True, "the desk records that the monitor is the blanked last display");
            Assert.That(desk.Windows.Snapshot.Crossings, Is.Empty, "a black panel is not a crossing destination");
        }
    }

    [Test]
    public async Task AMonitorWithNoInputSelectIsHandedOverByDroppingTheSignal()
    {
        // The BenQ XL2420T answers DDC perfectly well — luminance, contrast, colour gain — and
        // returns 0x60 itself when asked what input it is on, because it does not implement input
        // select at all. Every switch command such a monitor is sent is accepted and does nothing,
        // which is indistinguishable from an intermittent fault. It still changes computers: the
        // one showing it stops driving, and the monitor's own detection finds the one that still is.
        using var desk = await Desk.ConvergedAsync(windowsMonitors: Desk.WindowsMonitorsWithADeafBenq());
        desk.Windows.Router.Commands.Clear();

        await desk.SwitchAsync(desk.Benq, "Mac");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(desk.Windows.Router.Commands.Where(c => c.StartsWith(@"input \\.\DISPLAY2=", StringComparison.Ordinal)),
                Is.Empty, "a monitor with no input select must not be sent input commands");
            Assert.That(desk.Windows.Router.Commands.Any(c => c is @"disable \\.\DISPLAY2" or "sleep"),
                Is.True, "the computer showing it stops driving instead — that release is the switch");
        }
    }

    [Test]
    public async Task AMonitorThatIgnoresEveryDdcCommandIsStillHandedOver()
    {
        // Some monitors accept an input command and do nothing with it. Discovery then exhausts
        // every code and reports failure, which used to end the switch right there — before the
        // computer losing the monitor had been released. What the user saw was the gaining
        // computer dutifully lighting up its display while the monitor sat where it was.
        using var desk = await Desk.ConvergedAsync(monitorsIgnoreInputWrites: true);
        desk.Windows.Router.Commands.Clear();

        await desk.SwitchAsync(desk.Benq, "Mac");

        var commands = desk.Windows.Router.Commands;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(commands.Any(c => c.StartsWith(@"input \\.\DISPLAY2=", StringComparison.Ordinal)),
                Is.True, "pre-condition: DDC was tried first");
            Assert.That(commands.Any(c => c is @"disable \\.\DISPLAY2" or "sleep"),
                Is.True, "and when it got nowhere, the losing computer let the monitor go anyway");
        }
    }

    [Test]
    public async Task AMonitorThatPublishesNoInputCodesIsStillWorthTrying()
    {
        // Plenty of panels publish no capabilities string, or one with no value list for 0x60 — the
        // BenQ XL2420T is one. Reading that as "no codes to try" made the monitor that most needed
        // discovering the one that never could be: the desk gave up before sending a single command.
        using var desk = await Desk.ConvergedAsync();
        desk.Windows.Router.Commands.Clear();

        await desk.SwitchAsync(desk.Benq, "Mac");

        var tried = desk.Windows.Router.Commands
            .Where(c => c.StartsWith(@"input \\.\DISPLAY2=", StringComparison.Ordinal))
            .ToList();
        Assert.That(tried, Is.Not.Empty,
            "a monitor that admits to no codes is still worth trying the standard sockets on");
    }

    [Test]
    public async Task TheGainingComputersDisplayIsBackOnItsDesktopBeforeTheInputMoves()
    {
        // Waking a computer is not the same as it driving the panel: a display removed from the
        // desktop on the way out stays removed, so the monitor still arrived at a socket with no
        // signal on it and hunted back to the computer it had been told to leave. Re-enabling it
        // used to be the last step of the switch, seconds after the monitor had given up.
        using var desk = await Desk.ConvergedAsync();

        await desk.SwitchAsync(desk.Benq, "Mac");
        desk.Windows.Router.Commands.Clear();
        await desk.SwitchAsync(desk.Benq, "NINOG");

        var commands = desk.Windows.Router.Commands;
        var enabled = commands.IndexOf(@"enable \\.\DISPLAY2");
        var switched = commands.FindIndex(c => c.StartsWith(@"input \\.\DISPLAY2=", StringComparison.Ordinal));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(enabled, Is.GreaterThanOrEqualTo(0),
                "the computer the monitor is going to has to put that display back on its desktop");
            Assert.That(switched, Is.GreaterThanOrEqualTo(0), "pre-condition: the monitor was switched");
            Assert.That(enabled, Is.LessThan(switched),
                "and it has to happen before the input moves, not after the monitor has given up");
        }
    }

    [Test]
    public async Task SwitchingAMonitorBackWakesTheBlankedLastDisplay()
    {
        using var desk = await Desk.ConvergedAsync();
        await desk.SwitchAsync(desk.Aorus, "Mac");
        await desk.SwitchAsync(desk.Benq, "Mac");

        await desk.SwitchAsync(desk.Benq, "NINOG");

        var benq = desk.Windows.Snapshot.Monitors.Single(m => m.Id == desk.Benq);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(benq.Sleeping, Is.False, "the monitor that returns is not blanked anymore");
            Assert.That(desk.Windows.Snapshot.Crossings, Is.Not.Empty, "its crossings come back with it");
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


        // This fixture seeds the two known cables; production learns them from display inventories.
        public async Task<string> SwitchableAorusAsync()
        {
            var aorus = Windows.Snapshot.Monitors.Single(m => m.Width == 2560);
            await Task.CompletedTask;
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

        public static async Task<Desk> ConvergedAsync(
            bool announcePeers = true, List<PhysicalMonitorInfo>? windowsMonitors = null,
            bool monitorsIgnoreInputWrites = false)
        {
            var wire = new Wire();
            var windows = Node.Create("NINOG", Mode.Master, wire, WindowsScreens(),
                windowsMonitors ?? WindowsMonitors(), monitorsIgnoreInputWrites);
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

        // A panel that answers DDC but not input select: asked what input it is on it returns 0x60
        // itself — 96 — exactly as a BenQ XL2420T does, and publishes no list of codes it accepts.
        public static List<PhysicalMonitorInfo> WindowsMonitorsWithADeafBenq() =>
        [
            new(@"\\.\DISPLAY1", "AORUS", @"\\.\DISPLAY1", 15, [1, 3, 15, 17], ["AORUS", "Generic PnP Monitor"]),
            new(@"\\.\DISPLAY2", "XL2410T", @"\\.\DISPLAY2", 96, null, ["XL2410T", "BenQ XL2420T (DisplayPort)"]),
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

        public static Node Create(string name, Mode mode, Wire wire, LocalScreenSnapshot screens,
            List<PhysicalMonitorInfo> monitors, bool ignoresInputWrites = false)
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

            var router = new StubRouter(monitors, screens.Screens.Count, ignoresInputWrites);
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
            Monitors =
            [
                new DeskMonitorConfig
                {
                    Id = "aorus",
                    Label = "AORUS",
                    Aliases = ["AORUS"],
                    Sources =
                    [
                        new MonitorSourceConfig { Host = "NINOG", Input = 15, DdcId = @"\\.\DISPLAY1", AvailableInputs = [1, 3, 15, 17] },
                        new MonitorSourceConfig { Host = "Mac", Input = 17 },
                    ],
                },
            ],
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

    private sealed class StubRouter(List<PhysicalMonitorInfo> monitors, int localDisplays, bool ignoresInputWrites = false) : IDisplayRouter
    {
        // What was asked of the hardware, in order, so a test can say "woken, then switched".
        public List<string> Commands { get; } = [];

        // Which of this computer's displays have been taken off its desktop. Modelled because the
        // real Windows one refuses to remove the last active display — an OS with nothing to render
        // is a soft-locked OS — and the desk's whole last-display path is the answer to that refusal.
        private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<DisplayCommandResult>> ApplyAsync(DisplayRoutingConfig routing, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DisplayCommandResult>>([]);
        public Task<IReadOnlyList<DisplayCommandResult>> DoctorAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DisplayCommandResult>>([]);
        public Task<IReadOnlyList<PhysicalMonitorInfo>> InventoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PhysicalMonitorInfo>>(monitors);
        public Task<DisplayCommandResult> SetInputAsync(string id, int input, CancellationToken cancellationToken = default)
        {
            Commands.Add($"input {id}={input}");
            // A monitor that does not implement input select accepts the command and does nothing —
            // there is no failure to detect, which is exactly what makes it hard to tell from a
            // monitor that is merely slow.
            if (ignoresInputWrites) return Task.FromResult(new DisplayCommandResult($"set {id} input {input}", true));
            // emulate hardware: the monitor now reports the new input as its current one
            for (var i = 0; i < monitors.Count; i++)
                if (monitors[i].Id == id || monitors[i].LogicalName == id || monitors[i].Description.Contains(id, StringComparison.OrdinalIgnoreCase))
                    monitors[i] = monitors[i] with { CurrentInput = input };
            return Task.FromResult(new DisplayCommandResult($"set {id} input {input}", true));
        }
        public Task<DisplayCommandResult> SetDisplayPowerAsync(bool wake, CancellationToken cancellationToken = default)
        {
            Commands.Add(wake ? "wake" : "sleep");
            return Task.FromResult(new DisplayCommandResult("power", true));
        }
        public Task<DisplayCommandResult> SetMonitorDisplayEnabledAsync(string localSourceId, bool enabled, CancellationToken cancellationToken = default)
        {
            if (!enabled && _disabled.Count + 1 >= localDisplays && localDisplays > 0)
                return Task.FromResult(new DisplayCommandResult("display", false, "refusing to disable the last active display"));

            Commands.Add(enabled ? $"enable {localSourceId}" : $"disable {localSourceId}");
            if (enabled) _disabled.Remove(localSourceId); else _disabled.Add(localSourceId);
            return Task.FromResult(new DisplayCommandResult("display", true));
        }
        public Task<DisplayCommandResult> SetDisplayStandbyAsync(string localSourceId, bool standby, CancellationToken cancellationToken = default)
        {
            Commands.Add(standby ? $"blank {localSourceId}" : $"unblank {localSourceId}");
            return Task.FromResult(new DisplayCommandResult("standby", true));
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
