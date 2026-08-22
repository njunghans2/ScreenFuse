using Hydra.Config;
using Hydra.Desk;
using Hydra.Relay;

namespace Tests.Desk;

// Built from a desk that actually went wrong: a Windows machine (NINOG) and a Mac sharing an AORUS
// and a BenQ. It produced six monitors for three panels, two spellings of the Mac's name, entries
// stacked on the same coordinates, and a monitor with no computer on it.
public class DeskRealDeskTests
{
    // Windows names the AORUS "Generic PnP Monitor"; the monitor's own capabilities string says
    // "AORUS"; macOS says "AORUS FI27Q-X". All three are one panel.
    private static DeskInventoryMessage Windows() => new(
        [
            new DeskMonitorReport(@"\\.\DISPLAY1", "AORUS", 15, ["AORUS", "Generic PnP Monitor"], [1, 3, 15, 17]),
            new DeskMonitorReport(@"\\.\DISPLAY2", "XL2410T", 15, ["XL2410T", "BenQ XL2420T (DisplayPort)"], [1, 3, 17, 18, 15]),
        ],
        [
            new DeskScreenReport("NINOG:0", @"\\.\DISPLAY1", null, 0, 0, 2560, 1440),
            new DeskScreenReport("NINOG:1", @"\\.\DISPLAY2", null, 2560, 0, 1920, 1080),
        ]);

    private static DeskInventoryMessage Mac() => new(
        [],
        [
            new DeskScreenReport("Mac:0", null, "Built-in Retina Display", 0, 0, 1352, 878),
            new DeskScreenReport("Mac:1", null, "AORUS FI27Q-X", 1352, 0, 1920, 1080),
            new DeskScreenReport("Mac:2", null, "BenQ XL2420T", 3272, 0, 1920, 1080),
        ]);

    [Test]
    public void ThreePanelsSeenByTwoComputersStayThreeMonitors()
    {
        var merged = DeskMerge.Merge([], new Dictionary<string, DeskInventoryMessage>
        {
            ["NINOG"] = Windows(),
            ["Mac"] = Mac(),
        }, canonicalHosts: ["NINOG", "Mac"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(merged.Monitors, Has.Count.EqualTo(3));
            Assert.That(merged.Monitors.Select(m => m.Label), Does.Contain("AORUS FI27Q-X"),
                "the fullest name wins over 'AORUS' and 'Generic PnP Monitor'");
            Assert.That(merged.Monitors.Select(m => m.Label), Does.Contain("Built-in Retina Display"));
        }
    }

    [Test]
    public void TheWindowsMachineLearnsItsOwnInputCodes()
    {
        var merged = DeskMerge.Merge([], new Dictionary<string, DeskInventoryMessage>
        {
            ["NINOG"] = Windows(),
            ["Mac"] = Mac(),
        }, canonicalHosts: ["NINOG", "Mac"]);

        var aorus = merged.Monitors.Single(m => m.Label!.Contains("AORUS"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(aorus.Source("NINOG")!.Input, Is.EqualTo(15));
            Assert.That(aorus.Source("NINOG")!.AvailableInputs, Is.EqualTo(new[] { 1, 3, 15, 17 }));
            Assert.That(aorus.Source("Mac")!.Input, Is.Null, "the Mac cannot read this monitor, so nothing is guessed for it");
        }
    }

    [Test]
    public void NoTwoMonitorsEndUpOnTopOfEachOther()
    {
        var merged = DeskMerge.Merge([], new Dictionary<string, DeskInventoryMessage>
        {
            ["NINOG"] = Windows(),
            ["Mac"] = Mac(),
        }, canonicalHosts: ["NINOG", "Mac"]);

        AssertNoOverlaps(merged.Monitors);
    }

    [Test]
    public void EveryMonitorHasAComputerOnIt()
    {
        var merged = DeskMerge.Merge([], new Dictionary<string, DeskInventoryMessage>
        {
            ["NINOG"] = Windows(),
            ["Mac"] = Mac(),
        }, canonicalHosts: ["NINOG", "Mac"]);

        Assert.That(merged.Views.Where(v => v.ActiveHost == null), Is.Empty);
    }

    [Test]
    public void WhenBothComputersCanSeeAMonitorTheLiveInputDecidesWhoIsOnIt()
    {
        // Both machines reach the AORUS. VCP 0x60 answers 17 to whoever asks, and 17 is the Mac's
        // known code — so the Mac is the one on screen, however the reports happen to be ordered.
        var known = new List<DeskMonitorConfig>
        {
            new()
            {
                Id = "aorus", Label = "AORUS FI27Q-X", Aliases = ["AORUS FI27Q-X", "AORUS"],
                Width = 2560, Height = 1440, DeskX = 0, DeskY = 0,
                Sources =
                [
                    new MonitorSourceConfig { Host = "NINOG", Input = 15, DdcId = @"\\.\DISPLAY1", ScreenId = @"\\.\DISPLAY1" },
                    new MonitorSourceConfig { Host = "Mac", Input = 17, DdcId = "1", ScreenId = "AORUS FI27Q-X" },
                ],
            },
        };
        var reports = new Dictionary<string, DeskInventoryMessage>
        {
            ["NINOG"] = new([new DeskMonitorReport(@"\\.\DISPLAY1", "AORUS", 17, ["AORUS"], null)], []),
            ["Mac"] = new([new DeskMonitorReport("1", "AORUS FI27Q-X", 17, ["AORUS FI27Q-X"], null)], []),
        };

        var merged = DeskMerge.Merge(known, reports, canonicalHosts: ["NINOG", "Mac"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(merged.Views.Single().ActiveHost, Is.EqualTo("Mac"));
            Assert.That(merged.Monitors.Single().Source("NINOG")!.Input, Is.EqualTo(15),
                "an ambiguous reading must not overwrite a code that was learned unambiguously");
        }
    }

    [Test]
    public void ADeskThatAlreadyWentWrongIsRepairedInPlace()
    {
        // The six-entry desk as it was actually written: duplicates, two spellings of "Mac", and
        // three monitors sharing one pair of coordinates.
        var broken = new List<DeskMonitorConfig>
        {
            Monitor("generic-pnp-monitor", "Generic PnP Monitor", 0, 7, 2560, 1440,
                [Source("Mac", 15), Source("NINOG", 15, @"\\.\DISPLAY1", "NINOG:0")]),
            Monitor("benq-xl2420t-displayport", "BenQ XL2420T (DisplayPort)", 2560, 140, 1920, 1080,
                [Source("NINOG", 15, @"\\.\DISPLAY2", "NINOG:1")]),
            Monitor("built-in-retina-display", "Built-in Retina Display", -1352, 286, 1352, 878,
                [Source("Mac", 2, null, "Mac")]),
            Monitor("built-in-retina-display-2", "Built-in Retina Display", -1352, 286, 1352, 878,
                [Source("mac", null, null, "Mac:0")]),
            Monitor("aorus-fi27q-x", "AORUS FI27Q-X", 277, 286, 1920, 1080,
                [Source("mac", null, null, "Mac:1")]),
            Monitor("benq-xl2420t", "BenQ XL2420T", 2560, 140, 1920, 1080,
                [Source("mac", null, null, "Mac:2")]),
        };

        var merged = DeskMerge.Merge(broken, new Dictionary<string, DeskInventoryMessage>
        {
            ["NINOG"] = Windows(),
            ["Mac"] = Mac(),
        }, canonicalHosts: ["NINOG", "Mac"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(merged.Monitors, Has.Count.EqualTo(3), "six entries described three panels");
            Assert.That(merged.Monitors.SelectMany(m => m.Sources).Select(s => s.Host).Distinct(),
                Is.EquivalentTo(new[] { "NINOG", "Mac" }), "'mac' and 'Mac' are one computer");
            Assert.That(merged.ConfigChanged, Is.True, "the repair has to be written back");
        }
        AssertNoOverlaps(merged.Monitors);
    }

    [Test]
    public void AMonitorStillTalkingOnAnInputItIsNotShowingDoesNotStealTheOtherComputersCode()
    {
        // NINOG can still reach the monitor over HDMI, but the monitor is showing input 17 — the
        // Mac's. NINOG must not conclude that 17 is its own code.
        var known = new List<DeskMonitorConfig>
        {
            new()
            {
                Id = "aorus", Label = "AORUS FI27Q-X", Aliases = ["AORUS FI27Q-X"],
                Width = 2560, Height = 1440,
                Sources =
                [
                    new MonitorSourceConfig { Host = "NINOG", Input = 15, DdcId = @"\\.\DISPLAY1" },
                    new MonitorSourceConfig { Host = "Mac", Input = 17 },
                ],
            },
        };
        var reports = new Dictionary<string, DeskInventoryMessage>
        {
            ["NINOG"] = new([new DeskMonitorReport(@"\\.\DISPLAY1", "AORUS", 17, ["AORUS"], null)], []),
        };

        var merged = DeskMerge.Merge(known, reports, canonicalHosts: ["NINOG", "Mac"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(merged.Monitors.Single().Source("NINOG")!.Input, Is.EqualTo(15));
            Assert.That(merged.Views.Single().ActiveHost, Is.EqualTo("Mac"));
        }
    }

    [Test]
    public void AGenericNameNeverMergesTwoDifferentMonitors()
    {
        var reports = new Dictionary<string, DeskInventoryMessage>
        {
            ["pc"] = new(
                [
                    new DeskMonitorReport("A", "Generic PnP Monitor", 15, null, null),
                    new DeskMonitorReport("B", "Generic PnP Monitor", 15, null, null),
                ],
                [
                    new DeskScreenReport("pc:0", "A", null, 0, 0, 1920, 1080),
                    new DeskScreenReport("pc:1", "B", null, 1920, 0, 1920, 1080),
                ]),
        };

        var merged = DeskMerge.Merge([], reports, canonicalHosts: ["pc"]);

        Assert.That(merged.Monitors, Has.Count.EqualTo(2));
        AssertNoOverlaps(merged.Monitors);
    }

    [Test]
    public void NamesThatShareAModelAreRecognisedAcrossOperatingSystems()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DeskMerge.SameMonitor(["AORUS"], ["AORUS FI27Q-X"]), Is.True);
            Assert.That(DeskMerge.SameMonitor(["BenQ XL2420T (DisplayPort)"], ["BenQ XL2420T"]), Is.True);
            Assert.That(DeskMerge.SameMonitor(["Generic PnP Monitor"], ["Generic PnP Monitor"]), Is.False);
            Assert.That(DeskMerge.SameMonitor(["AORUS FI27Q-X"], ["BenQ XL2420T"]), Is.False);
            Assert.That(DeskMerge.BestLabel(["Generic PnP Monitor", "AORUS", "AORUS FI27Q-X"]), Is.EqualTo("AORUS FI27Q-X"));
            Assert.That(DeskMerge.BestLabel(["XL2410T", "BenQ XL2420T (DisplayPort)"]), Is.EqualTo("BenQ XL2420T (DisplayPort)"));
        }
    }

    private static void AssertNoOverlaps(IReadOnlyList<DeskMonitorConfig> monitors)
    {
        foreach (var a in monitors)
        {
            foreach (var b in monitors)
            {
                if (ReferenceEquals(a, b)) continue;
                var overlapX = Math.Min(a.DeskX + a.Width, b.DeskX + b.Width) - Math.Max(a.DeskX, b.DeskX);
                var overlapY = Math.Min(a.DeskY + a.Height, b.DeskY + b.Height) - Math.Max(a.DeskY, b.DeskY);
                Assert.That(overlapX <= 0 || overlapY <= 0, Is.True,
                    $"'{a.Label}' and '{b.Label}' overlap on the desk");
            }
        }
    }

    private static DeskMonitorConfig Monitor(string id, string label, int x, int y, int w, int h, List<MonitorSourceConfig> sources) =>
        new() { Id = id, Label = label, DeskX = x, DeskY = y, Width = w, Height = h, Sources = sources };

    private static MonitorSourceConfig Source(string host, int? input, string? ddcId = null, string? screenId = null) =>
        new() { Host = host, Input = input, DdcId = ddcId, ScreenId = screenId };
}
