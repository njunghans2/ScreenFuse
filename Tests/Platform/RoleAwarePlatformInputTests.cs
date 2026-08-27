using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;
using NUnit.Framework;
using Tests.Setup;

namespace Tests.Platform;

// The two halves of the desk both run on every computer, and this picks which one steers the
// cursor. Getting the choice wrong does not fail loudly — the cursor simply stops coming back, or
// stops crossing — so both directions are pinned here.
[TestFixture]
public class RoleAwarePlatformInputTests
{
    private static (RoleAwarePlatformInput Input, RecordingCursor Handler, RecordingCursor Output, IHydraProfile Profile) Build()
    {
        var profile = TransitionTestHelper.Profile("mac", new HydraConfig
        {
            Mode = Mode.Slave,
            Controller = "pc",
            Hosts = [new HostConfig { Name = "mac" }, new HostConfig { Name = "pc" }],
        });
        var handler = new RecordingCursor();
        var output = new RecordingCursor();
        return (new RoleAwarePlatformInput(profile, new StubInput(handler), output), handler, output, profile);
    }

    [Test]
    public async Task TheCursorIsShownByWhicheverHalfHidIt()
    {
        // The controller hides the cursor through the input handler — on Windows that is the
        // shield, and the system cursors swapped out from under it. Hand the keyboard over while
        // the pointer is away and the show arrives in the other role; addressed to the output
        // handler it undoes nothing, and the cursor stays invisible.
        var (input, handler, output, profile) = Build();
        profile.ApplyController("mac");
        await input.HideCursor();

        profile.ApplyController("pc");
        await input.ShowCursor();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Hides, Is.EqualTo(1));
            Assert.That(handler.Shows, Is.EqualTo(1), "the half that hid the cursor is the half that gives it back");
            Assert.That(output.Shows, Is.Zero, "the other half never hid anything and has nothing to show");
        }
    }

    [Test]
    public async Task WarpingFollowsTheRoleEvenWhileTheCursorIsHidden()
    {
        // A computer that is following hides its cursor whenever no pointer is standing on it, so
        // it is almost always hidden by the time it takes control. Steering must not still be
        // pointed at the output handler then: the controller parks and measures its pointer
        // through the input handler, and without it the pointer crosses over and drifts straight
        // back off the far screen.
        var (input, handler, output, profile) = Build();
        await input.HideCursor();
        Assert.That(output.Hides, Is.EqualTo(1), "a follower hides its cursor through the output handler");

        profile.ApplyController("mac");
        input.WarpCursor(100, 200);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Warps, Is.EqualTo(1), "the controller steers its pointer through the input handler");
            Assert.That(output.Warps, Is.Zero);
        }
    }

    private sealed class RecordingCursor : ICursor
    {
        public int Hides, Shows, Warps;
        public ValueTask HideCursor() { Hides++; return ValueTask.CompletedTask; }
        public ValueTask ShowCursor() { Shows++; return ValueTask.CompletedTask; }
        public void WarpCursor(int x, int y) => Warps++;
        public (int X, int Y)? GetCursorPosition() => (0, 0);
    }

    // The real input handlers are platform code; only their cursor half matters here.
    private sealed class StubInput(ICursor cursor) : IPlatformInput
    {
        public bool IsOnVirtualScreen { get; set; }
        public ValueTask HideCursor() => cursor.HideCursor();
        public ValueTask ShowCursor() => cursor.ShowCursor();
        public void WarpCursor(int x, int y) => cursor.WarpCursor(x, y);
        public (int X, int Y)? GetCursorPosition() => cursor.GetCursorPosition();
        public bool AnyMouseButtonHeld() => false;
        public Task StartEventTap(Action<double, double> onMouseMove, Action<double, double>? onMouseDelta,
            Action<KeyEvent> onKeyEvent, Action<MouseButtonEvent> onMouseButton, Action<MouseScrollEvent> onMouseScroll,
            Action? onLocalActivity = null) => Task.CompletedTask;
        public void StopEventTap() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
