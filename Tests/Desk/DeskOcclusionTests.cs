using Hydra.Config;
using Hydra.Desk;
using Hydra.Screen;

namespace Tests.Desk;

// Three monitors in a row and the pointer skipped the middle one.
//
// The desk as it really stood: BenQ, then AORUS, then the MacBook, all in a line. Both Windows
// monitors ended up with a crossing to the Mac, because each was simply "to the left of" it — so
// leaving the BenQ's right edge teleported the pointer past the AORUS onto the MacBook, and coming
// back from the MacBook landed on whichever of the two happened to face it more squarely rather
// than the one actually next to it. Neither is what the desk shows.
public class DeskOcclusionTests
{
    // BenQ 8823..10743 | AORUS 10743..13303 | Built-in 13386.., copied from the config that failed.
    private static List<DeskMonitorConfig> Row() =>
    [
        Monitor("benq", "BenQ XL2420T", 8823, 1711, 1920, 1080, "NINOG"),
        Monitor("aorus", "AORUS", 10743, 1600, 2560, 1440, "NINOG"),
        Monitor("built-in", "Built-in Retina Display", 13386, 1798, 1352, 878, "Mac"),
    ];

    private static List<DeskArrangement.Placed> Place(IReadOnlyList<DeskMonitorConfig> desk) =>
        DeskArrangement.Place(desk, id => desk.First(m => m.Id == id).Sources[0].Host);

    [Test]
    public void ThePointerDoesNotJumpOverTheMonitorInBetween()
    {
        var hosts = DeskArrangement.BuildHosts(Place(Row()), ["NINOG", "Mac"]);

        var ninog = hosts.Single(h => h.Name == "NINOG");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ninog.Neighbours, Has.Count.EqualTo(1),
                "only the AORUS touches the Mac; the BenQ has the AORUS in the way");
            Assert.That(ninog.Neighbours[0].SourceScreen, Is.EqualTo("NINOG:aorus"));
            Assert.That(ninog.Neighbours[0].Direction, Is.EqualTo(Direction.Right));
        }
    }

    [Test]
    public void ComingBackLandsOnTheMonitorNextToIt()
    {
        var hosts = DeskArrangement.BuildHosts(Place(Row()), ["NINOG", "Mac"]);

        var mac = hosts.Single(h => h.Name == "Mac");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mac.Neighbours, Has.Count.EqualTo(1));
            Assert.That(mac.Neighbours[0].Direction, Is.EqualTo(Direction.Left));
            Assert.That(mac.Neighbours[0].DestScreen, Is.EqualTo("NINOG:aorus"),
                "the AORUS is the monitor beside it; the BenQ is two along");
        }
    }

    [Test]
    public void AMonitorInTheMiddleIsLeftAndRightOfTheSameComputer()
    {
        // AORUS | Built-in | BenQ. The Mac needs a crossing out of each side of its one screen, and
        // Windows needs one back from each of its two.
        List<DeskMonitorConfig> sandwich =
        [
            Monitor("aorus", "AORUS", 0, 0, 2560, 1440, "NINOG"),
            Monitor("built-in", "Built-in Retina Display", 2700, 300, 1352, 878, "Mac"),
            Monitor("benq", "BenQ XL2420T", 4200, 300, 1920, 1080, "NINOG"),
        ];

        var hosts = DeskArrangement.BuildHosts(Place(sandwich), ["NINOG", "Mac"]);
        var mac = hosts.Single(h => h.Name == "Mac");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mac.Neighbours.Select(n => n.Direction),
                Is.EquivalentTo(new[] { Direction.Left, Direction.Right }));
            Assert.That(mac.Neighbours.Single(n => n.Direction == Direction.Left).DestScreen,
                Is.EqualTo("NINOG:aorus"));
            Assert.That(mac.Neighbours.Single(n => n.Direction == Direction.Right).DestScreen,
                Is.EqualTo("NINOG:benq"),
                "going on rightwards off the middle screen reaches the far one, which is the whole "
                + "point of putting a screen in between");
        }
    }

    [Test]
    public void SwappingTwoMonitorsSwapsWhichOneIsTheNeighbour()
    {
        // The check the user asked for: putting the BenQ where the AORUS was must change which
        // monitor the pointer comes back to. Nothing else about the desk changes.
        var swapped = Row().Select(m => m.Id switch
        {
            "benq" => m.With(deskX: 10743, deskY: 1600),
            "aorus" => m.With(deskX: 8823, deskY: 1711),
            _ => m,
        }).ToList();

        var hosts = DeskArrangement.BuildHosts(Place(swapped), ["NINOG", "Mac"]);
        var mac = hosts.Single(h => h.Name == "Mac");

        Assert.That(mac.Neighbours.Single().DestScreen, Is.EqualTo("NINOG:benq"));
    }

    [Test]
    public void AMonitorThatOnlyPartlyBlocksLeavesTheRestFacing()
    {
        // A small monitor low down does not hide a tall one behind it. Blocking is for a monitor
        // that covers the whole span the other two share — anything less still leaves an edge, and
        // the nearer crossing is matched first anyway.
        List<DeskMonitorConfig> partial =
        [
            Monitor("a", "A", 0, 0, 1920, 2000, "NINOG"),
            Monitor("small", "Small", 2000, 1500, 800, 500, "NINOG"),
            Monitor("b", "B", 3000, 0, 1920, 2000, "Mac"),
        ];

        var hosts = DeskArrangement.BuildHosts(Place(partial), ["NINOG", "Mac"]);

        Assert.That(hosts.Single(h => h.Name == "NINOG").Neighbours.Select(n => n.SourceScreen),
            Does.Contain("NINOG:a"));
    }

    [Test]
    public void TwoMonitorsStackedToTheSideEachTakeTheirOwnPartOfTheEdge()
    {
        // Neither of the two hides the other — they are above and below each other — so one edge
        // has to carry both crossings, each over the stretch it actually faces.
        List<DeskMonitorConfig> stacked =
        [
            Monitor("wide", "Wide", 0, 0, 1920, 2160, "NINOG"),
            Monitor("top", "Top", 2000, 0, 1920, 1080, "Mac"),
            Monitor("bottom", "Bottom", 2000, 1080, 1920, 1080, "Mac"),
        ];

        var hosts = DeskArrangement.BuildHosts(Place(stacked), ["NINOG", "Mac"]);
        var right = hosts.Single(h => h.Name == "NINOG").Neighbours
            .Where(n => n.Direction == Direction.Right).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(right, Has.Count.EqualTo(2), "both screens are reachable from the same edge");
            Assert.That(right.Select(n => n.DestScreen), Is.EquivalentTo(new[] { "Mac:top", "Mac:bottom" }));
            Assert.That(right.Select(n => (n.SourceStart, n.SourceEnd)),
                Is.EquivalentTo(new[] { (0, 50), (50, 100) }),
                "each takes the half of the edge it faces");
        }
    }

    [Test]
    public void MonitorsWithAGapStillCrossWhenNothingIsBetweenThem()
    {
        // Guards the fix from overshooting: empty desk space is not an obstacle.
        List<DeskMonitorConfig> apart =
        [
            Monitor("a", "A", 0, 0, 1920, 1080, "NINOG"),
            Monitor("b", "B", 6000, 0, 1920, 1080, "Mac"),
        ];

        var hosts = DeskArrangement.BuildHosts(Place(apart), ["NINOG", "Mac"]);

        Assert.That(hosts.Single(h => h.Name == "NINOG").Neighbours.Single().Direction,
            Is.EqualTo(Direction.Right));
    }

    [Test]
    public void AMonitorStackedBehindAnotherIsNotAVerticalNeighbour()
    {
        // The same rule turned ninety degrees.
        List<DeskMonitorConfig> tower =
        [
            Monitor("bottom", "Bottom", 0, 2000, 1920, 1080, "NINOG"),
            Monitor("middle", "Middle", 0, 1000, 1920, 1000, "NINOG"),
            Monitor("top", "Top", 0, 0, 1920, 1000, "Mac"),
        ];

        var hosts = DeskArrangement.BuildHosts(Place(tower), ["NINOG", "Mac"]);
        var ninog = hosts.Single(h => h.Name == "NINOG");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ninog.Neighbours, Has.Count.EqualTo(1));
            Assert.That(ninog.Neighbours[0].SourceScreen, Is.EqualTo("NINOG:middle"));
            Assert.That(ninog.Neighbours[0].Direction, Is.EqualTo(Direction.Up));
        }
    }

    private static DeskMonitorConfig Monitor(string id, string label, int x, int y, int w, int h, string host) => new()
    {
        Id = id, Label = label, Aliases = [label], DeskX = x, DeskY = y, Width = w, Height = h,
        Sources = [new MonitorSourceConfig { Host = host, Input = 15, ScreenId = $"{host}:{id}" }],
    };
}
