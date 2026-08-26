using Hydra.Relay;

namespace Hydra.Platform;

// Headless console mode (no display server): there is nothing to inject input into, so input from
// the peers is dropped rather than failing.
public sealed class NullPlatformOutput : IPlatformOutput
{
    public void MoveMouse(int x, int y) { }
    public void MoveMouseRelative(int dx, int dy) { }
    public void InjectKey(KeyEventMessage msg) { }
    public void InjectMouseButton(MouseButtonMessage msg) { }
    public void InjectMouseScroll(MouseScrollMessage msg) { }
    public void Dispose() { }
}