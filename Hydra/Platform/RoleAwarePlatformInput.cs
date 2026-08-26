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
    // Which half hid the cursor, while it is hidden.
    //
    // Handing the keyboard over mid-crossing used to leave the cursor invisible. The controller
    // hides it through the input handler — on Windows that is the shield, and the system cursors
    // swapped out from under it — and the show that undoes it arrived after the role had already
    // changed, so it was addressed to the output handler, which had never hidden anything and had
    // nothing to give back. Hiding is remembered, and the matching show goes back to the same half
    // however long it takes and whatever the role is by then.
    private ICursor? _hiddenBy;

    private ICursor Steering => _hiddenBy ?? (profile.IsController ? handler : cursor);

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
        var side = Steering;
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
