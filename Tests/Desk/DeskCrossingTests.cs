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

    [Test]
    public void ACrossingNamesNoScreen()
    {
        var hosts = DeskArrangement.BuildHosts(Place(Desk()), ["NINOG", "Mac"]);

        var neighbours = hosts.SelectMany(h => h.Neighbours).ToList();
        Assert.That(neighbours, Is.Not.Empty);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(neighbours.Select(n => n.SourceScreen), Is.All.Null,
                "the outermost screen belongs to the operating system, not to the desk");
            Assert.That(neighbours.Select(n => n.DestScreen), Is.All.Null);
        }
    }

    [Test]
    public void TwoComputersGetExactlyOneCrossingBetweenThem()
    {
        var hosts = DeskArrangement.BuildHosts(Place(Desk()), ["NINOG", "Mac"]);

        var neighbours = hosts.SelectMany(h => h.Neighbours).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(neighbours, Has.Count.EqualTo(1));
            Assert.That(neighbours[0].Mirror, Is.True, "the way back is derived from the way out");
        }
    }

    [Test]
    public void TheCrossingFollowsTheLongestSharedEdge()
    {
        // The AORUS shares 1080px with the BenQ on its right and 878px with the built-in on its
        // left, so right is the direction the arrangement is really expressing.
        var hosts = DeskArrangement.BuildHosts(Place(Desk()), ["NINOG", "Mac"]);

        var ninog = hosts.Single(h => h.Name == "NINOG");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ninog.Neighbours, Has.Count.EqualTo(1));
            Assert.That(ninog.Neighbours[0].Direction, Is.EqualTo(Direction.Right));
            Assert.That(ninog.Neighbours[0].Name, Is.EqualTo("Mac"));
        }
    }

    [Test]
    public void TheReturnPathExistsOnceMirrorsAreExpanded()
    {
        var hosts = DeskArrangement.BuildHosts(Place(Desk()), ["NINOG", "Mac"]);
        HydraConfig.ExpandMirrors(hosts);

        var mac = hosts.Single(h => h.Name == "Mac");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mac.Neighbours, Has.Count.EqualTo(1));
            Assert.That(mac.Neighbours[0].Direction, Is.EqualTo(Direction.Left));
            Assert.That(mac.Neighbours[0].Name, Is.EqualTo("NINOG"));
        }
    }

    [Test]
    public void EveryComputerAppearsEvenWithNoMonitorsPlaced()
    {
        var hosts = DeskArrangement.BuildHosts([], ["NINOG", "Mac", "Spare"]);

        Assert.That(hosts.Select(h => h.Name), Is.EquivalentTo(new[] { "NINOG", "Mac", "Spare" }));
    }

    [Test]
    public void MonitorsThatDoNotTouchProduceNoCrossing()
    {
        List<DeskMonitorConfig> apart =
        [
            Monitor("a", "A", 0, 0, 1920, 1080, "NINOG"),
            Monitor("b", "B", 4000, 0, 1920, 1080, "Mac"),
        ];

        var hosts = DeskArrangement.BuildHosts(Place(apart), ["NINOG", "Mac"]);

        Assert.That(hosts.SelectMany(h => h.Neighbours), Is.Empty);
    }

    private static DeskMonitorConfig Monitor(string id, string label, int x, int y, int w, int h, string host) => new()
    {
        Id = id, Label = label, Aliases = [label], DeskX = x, DeskY = y, Width = w, Height = h,
        Sources = [new MonitorSourceConfig { Host = host, Input = 15 }],
    };
}
