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
                Sources = [new MonitorSourceConfig { Host = "pc", Input = 15, DdcId = "d", ScreenId = "pc:0" }],
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
