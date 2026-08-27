using Hydra.Relay;

namespace Hydra.Platform;

public interface IPlatformOutput : IDisposable
{
    void MoveMouse(int x, int y);
    void MoveMouseRelative(int dx, int dy);
    void InjectKey(KeyEventMessage msg);
    void InjectMouseButton(MouseButtonMessage msg);
    void InjectMouseScroll(MouseScrollMessage msg);
    bool IsAccessibilityTrusted() => true;
    Task WaitForAccessibilityTrusted(CancellationToken cancel) => Task.CompletedTask;

    // Where the pointer actually is, in desktop coordinates. Used by slaves to report their real
    // cursor position to the master, so the master can reconcile its virtual pointer with what the
    // user is actually looking at. Null when the platform cannot report it.
    (int X, int Y)? GetCursorPosition() => null;
}
