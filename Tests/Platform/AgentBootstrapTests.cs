using System.Runtime.Versioning;
using Hydra.Platform.MacOs;

namespace Tests.Platform;

// Uninstalling made reinstalling impossible.
//
// The uninstaller disables the launch agent, which it has to — booting the job out only ends it for
// this login, and without disabling it, logging in again brings ScreenFuse straight back. But being
// disabled is recorded in launchd's own database rather than in the plist, so it outlives deleting
// the file. Installing again then failed with "Bootstrap failed: 5: Input/output error", which says
// nothing at all about the job simply having been switched off.
[TestFixture]
[Platform("MacOsX")]
[SupportedOSPlatform("macos")]
public class AgentBootstrapTests
{
    [Test]
    public void TheJobIsEnabledBeforeItIsBootstrapped()
    {
        var sequence = AgentCommands.BootstrapSequence("gui/501", "app.screenfuse.agent", "/tmp/agent.plist");

        var enable = sequence.ToList().FindIndex(c => c.StartsWith("enable", StringComparison.Ordinal));
        var bootstrap = sequence.ToList().FindIndex(c => c.StartsWith("bootstrap", StringComparison.Ordinal));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(enable, Is.GreaterThanOrEqualTo(0),
                "an uninstall disables the job, so an install has to switch it back on");
            Assert.That(bootstrap, Is.GreaterThan(enable),
                "enabling after bootstrapping is too late — the bootstrap is what fails");
        }
    }

    [Test]
    public void AnyRunningCopyIsEndedBeforeTheNewOneStarts()
    {
        var sequence = AgentCommands.BootstrapSequence("gui/501", "app.screenfuse.agent", "/tmp/agent.plist");

        var bootout = sequence.ToList().FindIndex(c => c.StartsWith("bootout", StringComparison.Ordinal));
        var bootstrap = sequence.ToList().FindIndex(c => c.StartsWith("bootstrap", StringComparison.Ordinal));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bootout, Is.GreaterThanOrEqualTo(0));
            Assert.That(bootout, Is.LessThan(bootstrap),
                "installing over a running agent must replace it, not leave two");
        }
    }

    [Test]
    public void TheSequenceNamesTheJobItIsGiven()
    {
        var sequence = AgentCommands.BootstrapSequence("gui/42", "some.label", "/tmp/x.plist");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sequence, Has.Some.Contains("gui/42/some.label"));
            Assert.That(sequence, Has.Some.Contains("/tmp/x.plist"));
        }
    }
}
