using Hydra.Config;
using Hydra.Keyboard;
using Hydra.Mouse;

namespace Hydra.Platform;

// One IPlatformInput that behaves as either role, chosen per call rather than per process.
//
// The two roles want the cursor moved by different means. The controller drives its own cursor
// through the input handler, which on Windows owns the shield the cursor hides behind and the
// warp that goes with it. A computer that is following has no shield up and no pointer of its own
// to steer: its cursor is placed by the output handler, the same one that injects what the
// controller sends.
//
// Choosing between them at startup is what made handing the keyboard over a restart. This picks on
// every call instead, so the moment the desk says the role changed, the right one is already in
// use — and neither has to be created, destroyed, or told about it.
//
// The event tap is not part of the choice. It is installed once and stays: the controller routes
// from it, and a follower still watches it to notice the user is sitting at this machine.
internal sealed class RoleAwarePlatformInput(IHydraProfile profile, IPlatformInput handler, ICursor cursor)
    : IPlatformInput
{
    private ICursor Steering => profile.IsController ? handler : cursor;

    // Which half hid the cursor, while it is hidden — and nothing else.
    //
    // Hiding and showing must pair up across a role change: handing the keyboard over mid-crossing
    // left the cursor invisible because the controller hides it through the input handler (on
    // Windows the shield, with the system cursors swapped out from under it) and the show that
    // undoes it arrived after the role had flipped, addressed to an output handler that had hidden
    // nothing and had nothing to give back.
    //
    // Steering is deliberately not sticky. Making it so broke the other direction: a computer that
    // is following hides its cursor whenever no pointer is standing on it, so by the time it took
    // control every warp and every cursor read was still pinned to the output handler — and the
    // controller's pointer is parked and measured through the input handler. It crossed onto the
    // other computer and drifted straight back off. Where the cursor is steered follows the role;
    // only the show follows the hide.
    private ICursor? _hiddenBy;

    public bool IsOnVirtualScreen
    {
        get => handler.IsOnVirtualScreen;
        set => handler.IsOnVirtualScreen = value;
    }

    public ValueTask HideCursor()
    {
        var side = Steering;
        _hiddenBy = side;
        return side.HideCursor();
    }

    public ValueTask ShowCursor()
    {
        var side = _hiddenBy ?? Steering;
        _hiddenBy = null;
        return side.ShowCursor();
    }
    public void WarpCursor(int x, int y) => Steering.WarpCursor(x, y);
    public (int X, int Y)? GetCursorPosition() => Steering.GetCursorPosition();
    public bool CursorIsVisible => Steering.CursorIsVisible;

    // Parking is the controller's move — it is the shield handshake — and a follower has no park
    // point to reach, so it falls through to a plain warp by way of the steering cursor.
    public ValueTask WarpToPark(int x, int y)
    {
        if (profile.IsController) return handler.WarpToPark(x, y);
        cursor.WarpCursor(x, y);
        return ValueTask.CompletedTask;
    }

    public bool AnyMouseButtonHeld() => handler.AnyMouseButtonHeld();
    public bool IsAccessibilityTrusted() => handler.IsAccessibilityTrusted();
    public Task WaitForAccessibilityTrusted(CancellationToken cancel) => handler.WaitForAccessibilityTrusted(cancel);

    public Task StartEventTap(
        Action<double, double> onMouseMove, Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent, Action<MouseButtonEvent> onMouseButton, Action<MouseScrollEvent> onMouseScroll,
        Action? onLocalActivity = null) =>
        handler.StartEventTap(onMouseMove, onMouseDelta, onKeyEvent, onMouseButton, onMouseScroll, onLocalActivity);

    public void StopEventTap() => handler.StopEventTap();
    public Task RestartEventTap() => handler.RestartEventTap();

    public ValueTask DisposeAsync() => handler.DisposeAsync();
}
