using Hydra.Config;
using Hydra.Desk;
using Hydra.Screen;

namespace Tests.Desk;

public class DeskArrangementTests
{
    private static DeskMonitorConfig Monitor(string id, string host, int x, int y, int w = 1920, int h = 1080) => new()
    {
        Id = id, Label = id, DeskX = x, DeskY = y, Width = w, Height = h,
        Sources = [new MonitorSourceConfig { Host = host, Input = 15, DdcId = id, ScreenId = $"{host}:screen" }],
    };

    [Test]
    public void TwoTouchingMonitorsOnDifferentComputersBecomeACrossingBothWays()
    {
        var monitors = new List<DeskMonitorConfig> { Monitor("left", "mac", 0, 0), Monitor("right", "pc", 1920, 0) };
        var placed = DeskArrangement.Place(monitors, id => id == "left" ? "mac" : "pc");

        var hosts = DeskArrangement.BuildHosts(placed, ["mac", "pc"]);

        var mac = hosts.Single(h => h.Name == "mac");
        var pc = hosts.Single(h => h.Name == "pc");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mac.Neighbours.Single().Direction, Is.EqualTo(Direction.Right));
            Assert.That(mac.Neighbours.Single().Name, Is.EqualTo("pc"));
            Assert.That(mac.Neighbours.Single().DestScreen, Is.EqualTo("pc:screen"),
                "a crossing names the screen it arrives on, so a monitor between two others can be reached");
            Assert.That(pc.Neighbours.Single().Direction, Is.EqualTo(Direction.Left));
            Assert.That(pc.Neighbours.Single().Name, Is.EqualTo("mac"));
        }
    }

    [Test]
    public void MonitorsOnTheSameComputerDoNotBecomeACrossing()
    {
        var monitors = new List<DeskMonitorConfig> { Monitor("a", "pc", 0, 0), Monitor("b", "pc", 1920, 0) };
        var placed = DeskArrangement.Place(monitors, _ => "pc");

        var hosts = DeskArrangement.BuildHosts(placed, ["pc"]);

        Assert.That(hosts.Single().Neighbours, Is.Empty, "the operating system already moves the pointer between its own screens");
    }

    [Test]
    public void MonitorsOfDifferentHeightsMapTheSharedSpanOntoEachOne()
    {
        // A 1080-tall monitor beside a 2160-tall one, aligned at the top: the crossing covers all of
        // the short one and the upper half of the tall one, so the pointer leaves at the height it
        // arrives at rather than jumping.
        var monitors = new List<DeskMonitorConfig>
        {
            Monitor("short", "mac", 0, 0, 1920, 1080),
            Monitor("tall", "pc", 1920, 0, 1920, 2160),
        };
        var placed = DeskArrangement.Place(monitors, id => id == "short" ? "mac" : "pc");

        var neighbour = DeskArrangement.BuildHosts(placed, ["mac", "pc"]).Single(h => h.Name == "mac").Neighbours.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(neighbour.SourceStart, Is.EqualTo(0));
            Assert.That(neighbour.SourceEnd, Is.EqualTo(100));
            Assert.That(neighbour.DestStart, Is.EqualTo(0));
            Assert.That(neighbour.DestEnd, Is.EqualTo(50));
        }
    }

    [Test]
    public void ASmallGapLeftByADragStillCounts()
    {
        var monitors = new List<DeskMonitorConfig> { Monitor("left", "mac", 0, 0), Monitor("right", "pc", 1937, 0) };
        var placed = DeskArrangement.Place(monitors, id => id == "left" ? "mac" : "pc");

        var hosts = DeskArrangement.BuildHosts(placed, ["mac", "pc"]);

        Assert.That(hosts.Single(h => h.Name == "mac").Neighbours, Has.Count.EqualTo(1));
    }

    [Test]
    public void MonitorsFarApartStillCrossBecauseOneIsStillBesideTheOther()
    {
        // Distance is not the question. However much space is between them, one of these is to the
        // left of the other and the pointer should travel that way; an earlier rule required them
        // to touch, which left a sensible arrangement with nothing to cross at.
        var monitors = new List<DeskMonitorConfig> { Monitor("left", "mac", 0, 0), Monitor("right", "pc", 4000, 0) };
        var placed = DeskArrangement.Place(monitors, id => id == "left" ? "mac" : "pc");

        var hosts = DeskArrangement.BuildHosts(placed, ["mac", "pc"]);

        var mac = hosts.Single(h => h.Name == "mac");
        Assert.That(mac.Neighbours.Single().Direction, Is.EqualTo(Direction.Right));
    }

    [Test]
    public void EveryHostAppearsEvenWithoutACrossing()
    {
        var hosts = DeskArrangement.BuildHosts([], ["mac", "pc", "linux"]);
        Assert.That(hosts.Select(h => h.Name), Is.EquivalentTo(new[] { "mac", "pc", "linux" }));
    }
}
