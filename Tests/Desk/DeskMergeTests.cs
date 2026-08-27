using Hydra.Config;
using Hydra.Desk;
using Hydra.Relay;

namespace Tests.Desk;

public class DeskMergeTests
{
    private static DeskInventoryMessage Inventory(
        (string DdcId, string Description, int? Input)[] monitors,
        (string ScreenId, string? Output, string? DisplayName, int X, int Y, int W, int H)[] screens) =>
        new(
            monitors.Select(m => new DeskMonitorReport(m.DdcId, m.Description, m.Input)).ToList(),
            screens.Select(s => new DeskScreenReport(s.ScreenId, s.Output, s.DisplayName, s.X, s.Y, s.W, s.H)).ToList());

    [Test]
    public void AScreenTheDisplayServerWillNotNameIsStillTheMonitorItIs()
    {
        // macOS names a screen from AppKit's screen list, which is refreshed off a run loop a
        // background agent does not pump — so a display reconnected underneath it comes back with
        // no name at all. CoreGraphics still lists it and the panel is plainly lit, but with the
        // name gone there was nothing left to recognise it by: the monitor dropped out of the desk
        // as an unidentifiable phantom, and every crossing that named it went nowhere.
        //
        // The output name is the answer, and on macOS it is the display's UUID: stable across a
        // reconnect, a replug, and a display server in no mood to say what the panel is called.
        var uuid = "12214163-3425-481C-87D3-ED793FFC4DAC";
        var known = DeskMerge.Merge([], new Dictionary<string, DeskInventoryMessage>
        {
            ["mac"] = Inventory(
                [(uuid, "AORUS FI27Q-X", 17)],
                [("mac:1", uuid, "AORUS FI27Q-X", 0, 0, 2560, 1440)]),
        }).Monitors;
        Assert.That(known.Single().Label, Is.EqualTo("AORUS FI27Q-X"), "pre-condition: named to begin with");

        // the same panel, the same UUID, and nothing willing to say its name
        var merged = DeskMerge.Merge(known, new Dictionary<string, DeskInventoryMessage>
        {
            ["mac"] = Inventory(
                [(uuid, "AORUS FI27Q-X", 17)],
                [("mac:1", uuid, null, 0, 0, 2560, 1440)]),
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(merged.Monitors, Has.Count.EqualTo(1), "it is the monitor it always was, not a second one");
            Assert.That(merged.Views.Single().ActiveHost, Is.EqualTo("mac"), "and the computer driving it still is");
            Assert.That(merged.Monitors.Single().Source("mac")!.ScreenId, Is.EqualTo(uuid),
                "identified by something that survives the name going missing");
        }
    }

    [Test]
    public void LearnsTheInputCodeFromTheComputerLookingAtTheMonitor()
    {
        var reports = new Dictionary<string, DeskInventoryMessage>
        {
            ["pc"] = Inventory(
                [(@"\\.\DISPLAY2", "BenQ XL2420T", 15)],
                [("pc:0", @"\\.\DISPLAY2", null, 0, 0, 1920, 1080)]),
        };

        var merged = DeskMerge.Merge([], reports);

        var monitor = merged.Monitors.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(monitor.Label, Is.EqualTo("BenQ XL2420T"));
            Assert.That(monitor.Source("pc")!.Input, Is.EqualTo(15));
            Assert.That(monitor.Source("pc")!.DdcId, Is.EqualTo(@"\\.\DISPLAY2"));
            Assert.That(monitor.Width, Is.EqualTo(1920));
            Assert.That(merged.Views.Single().ActiveHost, Is.EqualTo("pc"));
            Assert.That(merged.ConfigChanged, Is.True);
        }
    }

    [Test]
    public void ADeadInputIsNotLearnedWhenTheComputerNeverActuallyShowsThePanel()
    {
        // The monitor sits on a socket nothing drives — the desk once sent it there — but it
        // still answers DDC. The computer reading it must not adopt that code as its own: the
        // next switch would send the panel back to the same black socket. Only a code with the
        // screen as evidence is learned.
        var reports = new Dictionary<string, DeskInventoryMessage>
        {
            ["mac"] = Inventory(
                [("1", "BenQ XL2420T", 1)],
                []),
        };

        var merged = DeskMerge.Merge([], reports);

        var monitor = merged.Monitors.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(monitor.Source("mac")!.Input, Is.Null,
                "a code read without the screen being present is a dead socket, not a learned input");
            Assert.That(merged.Views.Single().ActiveHost, Is.EqualTo("mac"),
                "the computer that can read the panel still counts as the one on it");
        }
    }

    [Test]
    public void TheSamePhysicalMonitorSeenFromTwoComputersStaysOneMonitor()
    {
        // The PC learned input 15 earlier. Now the monitor shows the Mac, so only the Mac can see it.
        var known = new List<DeskMonitorConfig>
        {
            new()
            {
                Id = "benq-xl2420t",
                Label = "BenQ XL2420T",
                Width = 1920, Height = 1080, DeskX = 0, DeskY = 0,
                Sources = [new MonitorSourceConfig { Host = "pc", Input = 15, DdcId = @"\\.\DISPLAY2", ScreenId = "pc:0" }],
            },
        };
        var reports = new Dictionary<string, DeskInventoryMessage>
        {
            ["mac"] = Inventory(
                [("1", "BenQ XL2420T", 17)],
                [("mac", null, "BenQ XL2420T", 0, 0, 1920, 1080)]),
        };

        var merged = DeskMerge.Merge(known, reports);

        var monitor = merged.Monitors.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(monitor.Sources, Has.Count.EqualTo(2));
            Assert.That(monitor.Source("pc")!.Input, Is.EqualTo(15), "the absent computer keeps its learned input");
            Assert.That(monitor.Source("mac")!.Input, Is.EqualTo(17));
            Assert.That(merged.Views.Single().ActiveHost, Is.EqualTo("mac"));
            Assert.That(merged.Views.Single().Switchable, Is.True);
        }
    }

    [Test]
    public void APanelWithNoDdcStillJoinsTheDeskButIsNotSwitchable()
    {
        var reports = new Dictionary<string, DeskInventoryMessage>
        {
            ["mac"] = Inventory([], [("mac", null, "Built-in Retina Display", 0, 0, 3024, 1964)]),
        };

        var merged = DeskMerge.Merge([], reports);

        var view = merged.Views.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(view.Label, Is.EqualTo("Built-in Retina Display"));
            Assert.That(view.ActiveHost, Is.EqualTo("mac"));
            Assert.That(view.Switchable, Is.False);
            Assert.That(view.Sources.Single().Input, Is.Null);
        }
    }

    [Test]
    public void NewMonitorsAreLaidOutSideBySideRatherThanOnTopOfEachOther()
    {
        var reports = new Dictionary<string, DeskInventoryMessage>
        {
            ["pc"] = Inventory(
                [(@"\\.\DISPLAY1", "Left panel", 15), (@"\\.\DISPLAY2", "Right panel", 15)],
                [("pc:0", @"\\.\DISPLAY1", null, 0, 0, 1920, 1080), ("pc:1", @"\\.\DISPLAY2", null, 1920, 0, 1920, 1080)]),
        };

        var merged = DeskMerge.Merge([], reports);

        var rects = merged.Monitors.ToDictionary(m => m.Label!, m => (m.DeskX, m.DeskY));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rects["Right panel"].DeskX - rects["Left panel"].DeskX, Is.EqualTo(1920),
                "the operating system's own offsets are preserved");
            Assert.That(rects["Left panel"].DeskY, Is.EqualTo(rects["Right panel"].DeskY));
        }
    }

    [Test]
    public void AnAlreadyPlacedDeskIsNotRearranged()
    {
        var known = new List<DeskMonitorConfig>
        {
            new()
            {
                Id = "benq", Label = "BenQ", Width = 1920, Height = 1080, DeskX = 4000, DeskY = 250,
                Sources = [new MonitorSourceConfig { Host = "pc", Input = 15, DdcId = "d", ScreenId = "d" }],
            },
        };
        var reports = new Dictionary<string, DeskInventoryMessage>
        {
            ["pc"] = Inventory([("d", "BenQ", 15)], [("pc:0", "d", null, 0, 0, 1920, 1080)]),
        };

        var merged = DeskMerge.Merge(known, reports);

        var monitor = merged.Monitors.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(monitor.DeskX, Is.EqualTo(4000));
            Assert.That(monitor.DeskY, Is.EqualTo(250));
            Assert.That(merged.ConfigChanged, Is.False, "an unchanged desk must not trigger a save and a peer push");
        }
    }
}
