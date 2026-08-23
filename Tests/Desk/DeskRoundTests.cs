using Hydra.Desk;

namespace Tests.Desk;

// A desk that has not run yet reported itself as a working desk with no monitors.
//
// Before the first round there is no snapshot, so a placeholder stands in — and it has to name some
// computer as the one holding the keyboard, so it names this one. Every diagnostic then printed
// that guess as a fact. On a machine whose desk loop was wedged, the report read "controller:
// NINOG, role: has the keyboard, 0 monitors", which describes a computer that is fine and simply
// has nothing set up. The truth was the opposite: the config was intact and the loop had never
// completed a single round.
//
// Hours went into the monitors and the config because the one line that would have pointed
// somewhere else was quietly asserting the wrong thing.
public class DeskRoundTests
{
    [Test]
    public void ADeskThatHasNotRunYetSaysSo()
    {
        var snapshot = DeskSnapshot.Empty("NINOG");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Ready, Is.False,
                "nothing in this snapshot was measured — it is a placeholder");
            Assert.That(snapshot.Monitors, Is.Empty);
        }
    }

    [Test]
    public void ARealSnapshotIsReady()
    {
        // Anything built from an actual round counts as settled, so the warning appears only when
        // it means something.
        var snapshot = new DeskSnapshot(
            Controller: "NINOG",
            LocalHost: "NINOG",
            Hosts: ["NINOG", "Mac"],
            ConnectedHosts: ["Mac"],
            Monitors: [],
            Scenes: ["Default"],
            CurrentScene: "Default",
            IsController: true);

        Assert.That(snapshot.Ready, Is.True);
    }
}
