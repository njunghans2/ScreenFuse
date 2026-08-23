using Hydra.Config;
using Hydra.Desk;
using Hydra.Relay;

namespace Tests.Desk;

// Learning which socket each computer is plugged into, when only one of them can ever be on screen.
//
// A monitor with two computers wired to it shows one of them at a time, and the one it is not
// showing usually cannot be asked anything — so the wiring has to be worked out from whatever is
// readable at the moment, and kept. What makes it delicate is that many monitors go on answering
// DDC on an input they are not displaying: the computer asking gets back the code of the input
// currently on screen, which is not its own. Read naively, that computer overwrites its own correct
// code with the other one's, and switching breaks for good in a way nothing else explains.
public class DeskInputLearningTests
{
    private static DeskInventoryMessage Inventory(
        (string DdcId, string Description, int? Input)[] monitors,
        (string ScreenId, string? Output, string? DisplayName, int X, int Y, int W, int H)[] screens) =>
        new(
            monitors.Select(m => new DeskMonitorReport(m.DdcId, m.Description, m.Input)).ToList(),
            screens.Select(s => new DeskScreenReport(s.ScreenId, s.Output, s.DisplayName, s.X, s.Y, s.W, s.H)).ToList());

    // The real AORUS: Windows on DisplayPort (15), the Mac on a socket nobody has identified yet.
    private static List<DeskMonitorConfig> Aorus(int? macInput = null) =>
    [
        new()
        {
            Id = "aorus",
            Label = "AORUS FI27Q-X",
            Aliases = ["AORUS FI27Q-X"],
            Width = 2560, Height = 1440, DeskX = 0, DeskY = 0,
            Sources =
            [
                new MonitorSourceConfig { Host = "NINOG", Input = 15, DdcId = @"\\.\DISPLAY1", ScreenId = @"\\.\DISPLAY1" },
                new MonitorSourceConfig { Host = "Mac", Input = macInput, DdcId = "12214163", ScreenId = "AORUS FI27Q-X" },
            ],
        },
    ];

    [Test]
    public void TheComputerReadingAMonitorDoesNotStealItsOwnCode()
    {
        // The monitor is showing the Mac. Windows keeps answering DDC and reports 17, which is the
        // input on screen — the Mac's, not its own. Windows already knows it is on 15.
        var reports = new Dictionary<string, DeskInventoryMessage>
        {
            ["NINOG"] = Inventory(
                [(@"\\.\DISPLAY1", "AORUS FI27Q-X", 17)],
                [("NINOG:0", @"\\.\DISPLAY1", null, 0, 0, 2560, 1440)]),
            ["Mac"] = Inventory(
                [],
                [("Mac:1", null, "AORUS FI27Q-X", 0, 0, 2560, 1440)]),
        };

        var merged = DeskMerge.Merge(Aorus(), reports);
        var monitor = merged.Monitors.Single();

        Assert.That(monitor.Source("NINOG")!.Input, Is.EqualTo(15),
            "Windows is on DisplayPort and stays there; the code it read belongs to whoever is on screen");
    }

    [Test]
    public void TheCodeOnScreenIsLearnedForTheComputerItBelongsTo()
    {
        // The whole point of flipping the input by hand: it is the one moment the wiring can be
        // worked out, so it has to be captured then and kept afterwards.
        var reports = new Dictionary<string, DeskInventoryMessage>
        {
            ["NINOG"] = Inventory(
                [(@"\\.\DISPLAY1", "AORUS FI27Q-X", 17)],
                [("NINOG:0", @"\\.\DISPLAY1", null, 0, 0, 2560, 1440)]),
            ["Mac"] = Inventory(
                [],
                [("Mac:1", null, "AORUS FI27Q-X", 0, 0, 2560, 1440)]),
        };

        var merged = DeskMerge.Merge(Aorus(), reports);
        var monitor = merged.Monitors.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(monitor.Source("Mac")!.Input, Is.EqualTo(17),
                "the only other computer wired to it, and the only one whose socket was unknown");
            Assert.That(merged.Views.Single().ActiveHost, Is.EqualTo("Mac"),
                "the monitor is showing the Mac, so that is who is on it");
        }
    }

    [Test]
    public void WhatWasLearnedSurvivesTheMonitorGoingBack()
    {
        // Switching back to Windows must not undo it: the Mac cannot be asked again until the next
        // time it is on screen, so anything forgotten here is forgotten for good.
        var afterLearning = Aorus(macInput: 17);
        var reports = new Dictionary<string, DeskInventoryMessage>
        {
            ["NINOG"] = Inventory(
                [(@"\\.\DISPLAY1", "AORUS FI27Q-X", 15)],
                [("NINOG:0", @"\\.\DISPLAY1", null, 0, 0, 2560, 1440)]),
        };

        var merged = DeskMerge.Merge(afterLearning, reports);
        var monitor = merged.Monitors.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(monitor.Source("Mac")!.Input, Is.EqualTo(17), "the Mac is away, not gone");
            Assert.That(monitor.Source("NINOG")!.Input, Is.EqualTo(15));
            Assert.That(merged.Views.Single().ActiveHost, Is.EqualTo("NINOG"));
        }
    }

    [Test]
    public void AComputerOnItsOwnStillLearnsItsCode()
    {
        // The ordinary case has to keep working: one computer, nothing to confuse it with.
        var reports = new Dictionary<string, DeskInventoryMessage>
        {
            ["NINOG"] = Inventory(
                [(@"\\.\DISPLAY1", "AORUS FI27Q-X", 15)],
                [("NINOG:0", @"\\.\DISPLAY1", null, 0, 0, 2560, 1440)]),
        };

        var merged = DeskMerge.Merge([], reports);

        Assert.That(merged.Monitors.Single().Source("NINOG")!.Input, Is.EqualTo(15));
    }

    [Test]
    public void AnUnknownCodeIsNotGuessedAtWhenMoreThanOneComputerCouldOwnIt()
    {
        // Three computers wired to one monitor, two of them unidentified: the code on screen belongs
        // to one of them and there is nothing here to say which. Guessing would wire the desk wrong
        // and be believed afterwards, so nothing is learned.
        List<DeskMonitorConfig> shared =
        [
            new()
            {
                Id = "aorus",
                Label = "AORUS FI27Q-X",
                Aliases = ["AORUS FI27Q-X"],
                Width = 2560, Height = 1440,
                Sources =
                [
                    new MonitorSourceConfig { Host = "NINOG", Input = 15, DdcId = @"\\.\DISPLAY1", ScreenId = @"\\.\DISPLAY1" },
                    new MonitorSourceConfig { Host = "Mac", Input = null, DdcId = "12214163", ScreenId = "AORUS FI27Q-X" },
                    new MonitorSourceConfig { Host = "Spare", Input = null, DdcId = "99", ScreenId = "spare-screen" },
                ],
            },
        ];
        var reports = new Dictionary<string, DeskInventoryMessage>
        {
            ["NINOG"] = Inventory(
                [(@"\\.\DISPLAY1", "AORUS FI27Q-X", 17)],
                [("NINOG:0", @"\\.\DISPLAY1", null, 0, 0, 2560, 1440)]),
        };

        var merged = DeskMerge.Merge(shared, reports);
        var monitor = merged.Monitors.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(monitor.Source("Mac")!.Input, Is.Null);
            Assert.That(monitor.Source("Spare")!.Input, Is.Null);
            Assert.That(monitor.Source("NINOG")!.Input, Is.EqualTo(15), "and still no stealing");
        }
    }

    [Test]
    public void ACodeAlreadySpokenForIsNotHandedToAnyoneElse()
    {
        // Windows reads its own code back while the Mac is known to be on 17. Nothing to learn, and
        // the monitor is showing Windows.
        var reports = new Dictionary<string, DeskInventoryMessage>
        {
            ["NINOG"] = Inventory(
                [(@"\\.\DISPLAY1", "AORUS FI27Q-X", 15)],
                [("NINOG:0", @"\\.\DISPLAY1", null, 0, 0, 2560, 1440)]),
        };

        var merged = DeskMerge.Merge(Aorus(macInput: 17), reports);
        var monitor = merged.Monitors.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(monitor.Source("Mac")!.Input, Is.EqualTo(17));
            Assert.That(monitor.Source("NINOG")!.Input, Is.EqualTo(15));
        }
    }
}
