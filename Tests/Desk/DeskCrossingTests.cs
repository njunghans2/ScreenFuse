using Hydra.Config;
using Hydra.Desk;
using Hydra.Screen;

namespace Tests.Desk;

// A desk that produced crossings the pointer could never reach: the Mac was on the BenQ, so the
// crossing to it was anchored to the AORUS's right edge — an edge in the middle of Windows'
// desktop, where the pointer just moves on to the next Windows screen and never leaves.
public class DeskCrossingTests
{
    // Built-in (Mac) | AORUS (NINOG) | BenQ (Mac), which is how the real desk was arranged.
    private static List<DeskMonitorConfig> Desk() =>
    [
        Monitor("built-in", "Built-in Retina Display", -1352, 140, 1352, 878, "Mac"),
        Monitor("aorus", "AORUS FI27Q-X", 0, 7, 2560, 1440, "NINOG"),
        Monitor("benq", "BenQ XL2420T", 2560, 140, 1920, 1080, "Mac"),
    ];

    private static List<DeskArrangement.Placed> Place(IReadOnlyList<DeskMonitorConfig> desk) =>
        DeskArrangement.Place(desk, id => desk.First(m => m.Id == id).Sources[0].Host);

    // The arrangement the user actually built: the MacBook's screen dragged in between the two
    // Windows monitors. One computer is then on the right at one of the other's monitors and on the
    // left at the other, which a single crossing per pair of computers cannot express — so the
    // pointer kept leaving on the left whichever way the desk was arranged.
    private static List<DeskMonitorConfig> Sandwich() =>
    [
        Monitor("aorus", "AORUS", 0, 0, 2560, 1440, "NINOG"),
        Monitor("built-in", "Built-in Retina Display", 2700, 300, 1352, 878, "Mac"),
        Monitor("benq", "BenQ XL2420T", 4200, 300, 1920, 1080, "NINOG"),
    ];

    [Test]
    public void AScreenBetweenTwoOthersIsReachedFromBothSides()
    {
        var desk = Sandwich();
        var hosts = DeskArrangement.BuildHosts(Place(desk), ["NINOG", "Mac"]);

        var ninog = hosts.Single(h => h.Name == "NINOG");
        var toMac = ninog.Neighbours.Where(n => n.Name == "Mac").ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(toMac.Select(n => n.Direction), Does.Contain(Direction.Right),
                "from the AORUS the Mac is to the right");
            Assert.That(toMac.Select(n => n.Direction), Does.Contain(Direction.Left),
                "from the BenQ the Mac is to the left");
            Assert.That(toMac.Select(n => n.SourceScreen).Distinct().Count(), Is.EqualTo(2),
                "the two crossings leave from different screens");
        }
    }

    [Test]
    public void TheScreenInTheMiddleCanGetBackToBoth()
    {
        var desk = Sandwich();
        var hosts = DeskArrangement.BuildHosts(Place(desk), ["NINOG", "Mac"]);

        var mac = hosts.Single(h => h.Name == "Mac");
        Assert.That(mac.Neighbours.Select(n => n.Direction), Is.EquivalentTo(new[] { Direction.Left, Direction.Right }),
            "a screen in the middle has a computer on either side of it");
    }

    [Test]
    public void ACrossingNamesTheScreenItLeavesFrom()
    {
        var hosts = DeskArrangement.BuildHosts(Place(Desk()), ["NINOG", "Mac"]);

        var neighbours = hosts.SelectMany(h => h.Neighbours).ToList();
        Assert.That(neighbours, Is.Not.Empty);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(neighbours.Select(n => n.SourceScreen), Is.All.Not.Null,
                "which screen the pointer leaves from is decided by the desk, not by the operating system");
            Assert.That(neighbours.Select(n => n.DestScreen), Is.All.Not.Null);
        }
    }

    [Test]
    public void EveryCrossingIsWrittenOutRatherThanMirrored()
    {
        var hosts = DeskArrangement.BuildHosts(Place(Desk()), ["NINOG", "Mac"]);

        var neighbours = hosts.SelectMany(h => h.Neighbours).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(neighbours, Is.Not.Empty);
            Assert.That(neighbours.Select(n => n.Mirror), Is.All.False,
                "both ways round are derived independently; a mirrored guess lands the pointer at the "
                + "wrong height between monitors of different sizes");
        }
    }

    [Test]
    public void OneScreenCanReachAnotherComputerInTwoDirectionsAtOnce()
    {
        // In this desk the AORUS has a Mac monitor on either side of it, so it needs a crossing both
        // ways. One crossing per pair of computers could only ever have said one of them.
        var hosts = DeskArrangement.BuildHosts(Place(Desk()), ["NINOG", "Mac"]);

        var ninog = hosts.Single(h => h.Name == "NINOG");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ninog.Neighbours, Has.Count.EqualTo(2));
            Assert.That(ninog.Neighbours.Select(n => n.Direction), Is.EquivalentTo(new[] { Direction.Left, Direction.Right }));
            Assert.That(ninog.Neighbours.Select(n => n.Name), Is.All.EqualTo("Mac"));
        }
    }

    [Test]
    public void TheWayBackExistsFromEveryScreenThatCanBeReached()
    {
        var hosts = DeskArrangement.BuildHosts(Place(Desk()), ["NINOG", "Mac"]);

        var mac = hosts.Single(h => h.Name == "Mac");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mac.Neighbours, Is.Not.Empty, "the pointer has to be able to come back");
            Assert.That(mac.Neighbours.Select(n => n.Name), Is.All.EqualTo("NINOG"));
            Assert.That(mac.Neighbours.Select(n => n.SourceScreen).Distinct().Count(), Is.EqualTo(2),
                "each of the Mac's screens returns from its own edge");
        }
    }

    [Test]
    public void EveryComputerAppearsEvenWithNoMonitorsPlaced()
    {
        var hosts = DeskArrangement.BuildHosts([], ["NINOG", "Mac", "Spare"]);

        Assert.That(hosts.Select(h => h.Name), Is.EquivalentTo(new[] { "NINOG", "Mac", "Spare" }));
    }

    [Test]
    public void MonitorsWithAGapBetweenThemStillCross()
    {
        // Two monitors with space between them are still one to the left of the other, exactly as
        // they sit on the desk. Requiring them to touch made a perfectly sensible arrangement do
        // nothing at all, with an empty neighbour list as the only evidence.
        List<DeskMonitorConfig> apart =
        [
            Monitor("a", "A", 0, 0, 1920, 1080, "NINOG"),
            Monitor("b", "B", 4000, 0, 1920, 1080, "Mac"),
        ];

        var hosts = DeskArrangement.BuildHosts(Place(apart), ["NINOG", "Mac"]);

        var ninog = hosts.Single(h => h.Name == "NINOG");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ninog.Neighbours, Has.Count.EqualTo(1));
            Assert.That(ninog.Neighbours[0].Direction, Is.EqualTo(Direction.Right));
        }
    }

    [Test]
    public void AMonitorAboveAnotherCrossesUpwards()
    {
        List<DeskMonitorConfig> stacked =
        [
            Monitor("below", "Below", 0, 1200, 1920, 1080, "NINOG"),
            Monitor("above", "Above", 0, 0, 1920, 1080, "Mac"),
        ];

        var hosts = DeskArrangement.BuildHosts(Place(stacked), ["NINOG", "Mac"]);

        var ninog = hosts.Single(h => h.Name == "NINOG");
        Assert.That(ninog.Neighbours.Single().Direction, Is.EqualTo(Direction.Up));
    }

    [Test]
    public void MonitorsSharingNoEdgeAtAllProduceNoCrossing()
    {
        // Diagonal: nothing of one faces anything of the other, so there is no sensible edge to
        // cross at and none is invented.
        List<DeskMonitorConfig> diagonal =
        [
            Monitor("a", "A", 0, 0, 1920, 1080, "NINOG"),
            Monitor("b", "B", 4000, 4000, 1920, 1080, "Mac"),
        ];

        var hosts = DeskArrangement.BuildHosts(Place(diagonal), ["NINOG", "Mac"]);

        Assert.That(hosts.SelectMany(h => h.Neighbours), Is.Empty);
    }

    private static DeskMonitorConfig Monitor(string id, string label, int x, int y, int w, int h, string host) => new()
    {
        Id = id, Label = label, Aliases = [label], DeskX = x, DeskY = y, Width = w, Height = h,
        // The screen identifier matters here: a crossing names the screen it leaves from, and two
        // crossings from the same computer are only distinguishable by it.
        Sources = [new MonitorSourceConfig { Host = host, Input = 15, ScreenId = $"{host}:{id}" }],
    };
}
