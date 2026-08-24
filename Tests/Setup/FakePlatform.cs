using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;

namespace Tests.Setup;

public sealed class FakePlatform : IPlatformInput, ICursorHider
{
    private Action<double, double>? _onMouseMove;
    private Action<double, double>? _onMouseDelta;
    private Action<KeyEvent>? _onKeyEvent;
    private Action<MouseButtonEvent>? _onMouseButton;
    private Action<MouseScrollEvent>? _onMouseScroll;

    public bool IsOnVirtualScreen { get; set; }
    public bool HideCursorCalled { get; set; }
    public bool ShowCursorCalled { get; set; }
    public int WarpX { get; private set; }
    public int WarpY { get; private set; }

    // The desktop the cursor actually lives on. A real SetCursorPos cannot put the cursor outside
    // it: ask for a point on a monitor that is no longer there and the cursor lands on the edge of
    // one that is. That clamp is the whole reason a stale park point breaks the mouse, so the fake
    // has to do it too or the bug is invisible here.
    private (int X, int Y, int Width, int Height) _desktop = (0, 0, 2560, 1440);
    public (int X, int Y, int Width, int Height) Desktop
    {
        get => _desktop;
        set { _desktop = value; WarpCursor(WarpX, WarpY); }
    }

    // set to InputRouter.FlushAsync to synchronize channel consumer after each Fire call
    public Func<Task>? AfterFireCallback { get; set; }

    private void Flush() => AfterFireCallback?.Invoke().GetAwaiter().GetResult();

    public void FireMouseMove(double x, double y) { _onMouseMove?.Invoke(x, y); Flush(); }
    public void FireMouseDelta(double dx, double dy) { _onMouseDelta?.Invoke(dx, dy); Flush(); }
    public void FireKeyEvent(KeyEvent e) { _onKeyEvent?.Invoke(e); Flush(); }
    public void FireMouseButton(MouseButtonEvent e) { _onMouseButton?.Invoke(e); Flush(); }
    public void FireMouseScroll(MouseScrollEvent e) { _onMouseScroll?.Invoke(e); Flush(); }

    public void Reset()
    {
        IsOnVirtualScreen = false;
        HideCursorCalled = false;
        ShowCursorCalled = false;
        WarpCursor(Desktop.X + Desktop.Width / 2, Desktop.Y + Desktop.Height / 2);
    }

    public static List<DetectedScreen> GetAllScreens() => [new DetectedScreen(0, 0, 2560, 1440, null, null, null)];
    public bool IsAccessibilityTrusted() => true;

    public Task StartEventTap(
        Action<double, double> onMouseMove,
        Action<double, double>? onMouseDelta,
        Action<KeyEvent> onKeyEvent,
        Action<MouseButtonEvent> onMouseButton,
        Action<MouseScrollEvent> onMouseScroll,
        Action? onLocalActivity = null)
    {
        _onMouseMove = onMouseMove;
        _onMouseDelta = onMouseDelta;
        _onKeyEvent = onKeyEvent;
        _onMouseButton = onMouseButton;
        _onMouseScroll = onMouseScroll;
        WarpCursor(Desktop.X + Desktop.Width / 2, Desktop.Y + Desktop.Height / 2);
        return Task.CompletedTask;
    }

    public bool AnyMouseButtonHeld { get; set; }
    bool IPlatformInput.AnyMouseButtonHeld() => AnyMouseButtonHeld;
    public void StopEventTap() { }
    public void WarpCursor(int x, int y)
    {
        WarpX = Math.Clamp(x, Desktop.X, Desktop.X + Desktop.Width - 1);
        WarpY = Math.Clamp(y, Desktop.Y, Desktop.Y + Desktop.Height - 1);
    }
    // ICursorHider — what InputRouter calls
    void ICursorHider.Hide() { HideCursorCalled = true; }
    void ICursorHider.Show() { ShowCursorCalled = true; }

    public ValueTask HideCursor() { HideCursorCalled = true; return ValueTask.CompletedTask; }
    public ValueTask ShowCursor() { ShowCursorCalled = true; return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
