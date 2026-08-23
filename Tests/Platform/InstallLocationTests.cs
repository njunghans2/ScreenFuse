using System.Runtime.Versioning;
using Hydra.Platform.Windows;

namespace Tests.Platform;

// The installer registered the service against whatever folder the download happened to be run
// from. That made the installation only as durable as that folder — and when it was emptied, what
// was left was a service pointing at a binary that no longer existed: still running, still
// restarting itself every five seconds, and impossible to remove, because the executable that knew
// how to remove it was the one that had been deleted.
//
// So the payload is copied somewhere it belongs first, and the service is registered there.
[TestFixture]
[Platform("Win")]
[SupportedOSPlatform("windows")]
public class InstallLocationTests
{
    [Test]
    public void TheInstallLocationDoesNotDependOnWhereTheDownloadWasRunFrom()
    {
        var installDir = ServiceCommands.InstallDirectory();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(installDir, Does.EndWith("ScreenFuse"));
            Assert.That(Path.IsPathFullyQualified(installDir), Is.True);
            Assert.That(installDir, Does.Not.Contain("Downloads"),
                "the download folder is the user's to empty, and emptying it must not break anything");
        }
    }

    [Test]
    public void ThePayloadIsCopiedBesideTheInstalledExecutable()
    {
        var source = Path.Combine(Path.GetTempPath(), $"sf-src-{Guid.NewGuid():N}");
        var install = Path.Combine(Path.GetTempPath(), $"sf-dst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(source, "Resources"));
        File.WriteAllText(Path.Combine(source, "screenfuse.exe"), "binary");
        File.WriteAllText(Path.Combine(source, "screenfuse.pdb"), "symbols");
        File.WriteAllText(Path.Combine(source, "Resources", "shield.txt"), "nested");

        try
        {
            var installed = ServiceCommands.CopyPayload(Path.Combine(source, "screenfuse.exe"), install);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(installed, Is.EqualTo(Path.Combine(install, "screenfuse.exe")));
                Assert.That(File.Exists(installed), Is.True);
                Assert.That(File.Exists(Path.Combine(install, "screenfuse.pdb")), Is.True,
                    "a self-contained build is more than its executable");
                Assert.That(File.Exists(Path.Combine(install, "Resources", "shield.txt")), Is.True,
                    "including everything in folders beside it");
            }
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            if (Directory.Exists(install)) Directory.Delete(install, recursive: true);
        }
    }

    [Test]
    public void InstallingOverAnExistingInstallationIsARepairRatherThanAnError()
    {
        // Running --install from the installed copy is how someone repairs a broken registration.
        // Copying a folder onto itself would throw, and there is nothing to copy anyway.
        var install = Path.Combine(Path.GetTempPath(), $"sf-same-{Guid.NewGuid():N}");
        Directory.CreateDirectory(install);
        var exe = Path.Combine(install, "screenfuse.exe");
        File.WriteAllText(exe, "binary");

        try
        {
            Assert.That(ServiceCommands.CopyPayload(exe, install), Is.EqualTo(exe));
        }
        finally
        {
            Directory.Delete(install, recursive: true);
        }
    }

    [Test]
    public void TheSameFolderSpelledDifferentlyIsStillTheSameFolder()
    {
        var root = Path.GetTempPath();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ServiceCommands.SameDirectory(root, root.TrimEnd(Path.DirectorySeparatorChar)), Is.True,
                "a trailing separator is not a different place");
            Assert.That(ServiceCommands.SameDirectory(root.ToUpperInvariant(), root.ToLowerInvariant()), Is.True,
                "Windows paths do not care about case, and neither can this");
            Assert.That(ServiceCommands.SameDirectory(root, Path.Combine(root, "elsewhere")), Is.False);
        }
    }
}
