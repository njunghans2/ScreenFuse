using System.Text.RegularExpressions;

namespace Tests.Tray;

// ScreenFuse crashed on every launch on macOS with "IDispatcherImpl belongs to a different thread",
// and launchd restarted it each time, so the Mac sat in a crash loop: invisible in Activity Monitor,
// impossible to quit, never joining the desk.
//
// The cause was one await too early. A console app has no synchronisation context, so any await in
// startup hands the rest of the program to the thread pool — and the tray then initialises Avalonia,
// which on macOS has to happen on the thread the process started on. Nothing about the code looks
// wrong; the damage is done by the keyword.
//
// This reads the startup file rather than running it, because the fault only shows on a Mac and only
// as a crash. A runtime check in TrayApplication.Run catches it too, but by then it is too late to
// be a test.
public class StartupThreadTests
{
    private static string Startup()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Hydra.sln")))
            directory = directory.Parent;
        Assert.That(directory, Is.Not.Null, "could not find the repository root");
        return File.ReadAllText(Path.Combine(directory!.FullName, "Hydra", "Program.cs"));
    }

    [Test]
    public void StartupDoesNotAwaitBeforeTheTrayOpens()
    {
        var source = Startup();
        var tray = source.IndexOf("TrayApplication.Run(app.Services", StringComparison.Ordinal);
        Assert.That(tray, Is.GreaterThan(0), "the tray is no longer started here — this test needs rewriting");

        // Everything up to the tray, minus the blocks that exit the program before reaching it.
        var before = source[..tray];
        var found = Regex.Matches(before, @"^(?!\s*//).*?\bawait\b.*$", RegexOptions.Multiline)
            .Select(m => m.Value.Trim())
            .ToList();

        // The scanner has to be able to see an await at all, or this test passes whatever happens —
        // which is how the first version of the wheel tests were written, and they proved nothing.
        Assert.That(found, Is.Not.Empty,
            "the scanner found no awaits anywhere before the tray, which means it is not looking properly");

        var offenders = found
            // These sit inside `if (...) { ...; return; }` blocks for --doctor and --scene, so the
            // tray is never reached down those paths.
            .Where(line => !line.Contains("DoctorAsync()", StringComparison.Ordinal)
                           && !line.Contains("ActivateSceneFromCli", StringComparison.Ordinal))
            .ToList();

        Assert.That(offenders, Is.Empty,
            "an await here moves the rest of startup onto the thread pool, and the tray must open on "
            + "the main thread. Use Sync.Wait instead.");
    }

    [Test]
    public void TheHostIsStartedWithoutAwaiting()
    {
        var source = Startup();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Does.Contain("Sync.Wait(app.StartAsync())"));
            Assert.That(source, Does.Not.Contain("await app.StartAsync()"));
        }
    }
}
