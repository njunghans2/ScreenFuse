using Hydra.Config;
using Hydra.Desk;
using Hydra.Display;
using Hydra.Screen;

namespace Tests.Desk;

// A config is parsed with its mirrors already expanded and derived without them. Comparing the two
// as written makes an identical layout look like a change on every round — which, while the desk
// also restarted to apply changes, meant the controller rewrote its config and restarted every few
// seconds, long before the relay could connect. No peer ever joined and the desk stayed empty.
public class DeskTopologyStabilityTests
{
    [Test]
    public void AMirrorExpandedConfigMatchesTheLayoutItWasDerivedFrom()
    {
        var derived = new List<HostConfig>
        {
            new() { Name = "NINOG", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "Mac", Mirror = true }] },
            new() { Name = "Mac" },
        };

        // What the file looks like once it has been written and read back.
        var loaded = Clone(derived);
        HydraConfig.ExpandMirrors(loaded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Edges(loaded), Is.EqualTo(Edges(derived)),
                "an identical layout must compare equal whether or not its mirrors are expanded");
            Assert.That(loaded.Single(h => h.Name == "Mac").Neighbours, Is.Not.Empty,
                "expansion is what creates the way back");
        }
    }

    [Test]
    public void ADifferentLayoutStillCompesAsDifferent()
    {
        var right = new List<HostConfig>
        {
            new() { Name = "NINOG", Neighbours = [new NeighbourConfig { Direction = Direction.Right, Name = "Mac", Mirror = true }] },
            new() { Name = "Mac" },
        };
        var left = new List<HostConfig>
        {
            new() { Name = "NINOG", Neighbours = [new NeighbourConfig { Direction = Direction.Left, Name = "Mac", Mirror = true }] },
            new() { Name = "Mac" },
        };

        Assert.That(Edges(right), Is.Not.EqualTo(Edges(left)));
    }

    [Test]
    public void TheDerivedLayoutIsStableAcrossRepeatedRounds()
    {
        // Deriving twice from the same desk must produce the same answer, or the config is rewritten
        // and pushed to every peer forever.
        List<DeskMonitorConfig> desk =
        [
            Monitor("built-in", "Built-in Retina Display", -1352, 140, 1352, 878, "Mac"),
            Monitor("aorus", "AORUS FI27Q-X", 0, 7, 2560, 1440, "NINOG"),
            Monitor("benq", "BenQ XL2420T", 2560, 140, 1920, 1080, "Mac"),
        ];
        List<string> hosts = ["NINOG", "Mac"];

        var first = DeskArrangement.BuildHosts(DeskArrangement.Place(desk, id => desk.First(m => m.Id == id).Sources[0].Host), hosts);
        var second = DeskArrangement.BuildHosts(DeskArrangement.Place(desk, id => desk.First(m => m.Id == id).Sources[0].Host), hosts);

        var settled = Clone(first);
        HydraConfig.ExpandMirrors(settled);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Edges(second), Is.EqualTo(Edges(first)));
            Assert.That(Edges(settled), Is.EqualTo(Edges(first)), "a written-and-reloaded desk must not look changed");
        }
    }

    // m1ddc cannot always get a display's name, and prints "(null)" with the display's UUID.
    [Test]
    public void ANamelessDisplayIsNotCalledNull()
    {
        var monitors = DisplayRouter.ParseM1Ddc("[1] (null) (37D8832A-2D66-02CA-B9F7-8F30A301B230)");

        var monitor = monitors.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(monitor.Description, Is.EqualTo("Display 1"));
            Assert.That(monitor.Id, Is.EqualTo("37D8832A-2D66-02CA-B9F7-8F30A301B230"), "the UUID outlives the display number");
            Assert.That(monitor.Aliases, Does.Contain("37D8832A-2D66-02CA-B9F7-8F30A301B230"));
            Assert.That(monitor.Aliases, Does.Not.Contain("(null)"));
        }
    }

    [Test]
    public void ANamedDisplayKeepsItsName()
    {
        var monitors = DisplayRouter.ParseM1Ddc("[2] BenQ XL2420T (A1B2C3D4-5678-90AB-CDEF-1234567890AB)");

        var monitor = monitors.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(monitor.Description, Is.EqualTo("BenQ XL2420T"));
            Assert.That(monitor.Aliases, Does.Contain("BenQ XL2420T"));
        }
    }

    [Test]
    public void TwoNamelessDisplaysDoNotBecomeOneMonitor()
    {
        Assert.That(DeskMerge.SameMonitor(["(null)"], ["(null)"]), Is.False);
    }

    // Normalises before comparing, exactly as the desk does: expand mirrors on a copy, so a layout
    // that has been written and read back compares equal to the one it was derived from.
    private static List<string> Edges(List<HostConfig> hosts)
    {
        var normalised = Clone(hosts);
        HydraConfig.ExpandMirrors(normalised);
        return normalised
            .SelectMany(h => h.Neighbours.Select(n => $"{h.Name}|{n.Direction}|{n.Name}".ToLowerInvariant()))
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    private static List<HostConfig> Clone(List<HostConfig> hosts) => hosts.Select(h => new HostConfig
    {
        Name = h.Name,
        Neighbours = h.Neighbours.Select(n => new NeighbourConfig
        {
            Direction = n.Direction, Name = n.Name, Mirror = n.Mirror,
            SourceScreen = n.SourceScreen, DestScreen = n.DestScreen,
        }).ToList(),
    }).ToList();

    private static DeskMonitorConfig Monitor(string id, string label, int x, int y, int w, int h, string host) => new()
    {
        Id = id, Label = label, Aliases = [label], DeskX = x, DeskY = y, Width = w, Height = h,
        Sources = [new MonitorSourceConfig { Host = host, Input = 15 }],
    };
}
