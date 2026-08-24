using Hydra.FileTransfer;
using Hydra.Keyboard;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Screen;

// While the pointer is away, the input hooks swallow every local key and click. They do it on this
// router's promise that it is forwarding them somewhere. The loop that keeps that promise is a
// single consumer, so anything that blocks it -- a clipboard the owning app is slow to render, a
// file manager that will not answer, a deadlock, a bug -- stops the forwarding without stopping the
// swallowing. What the person at the desk sees is a computer with no keyboard and no mouse.
//
// Nothing inside the loop can rescue that, because the loop is the thing that stopped. So it is
// watched from outside, and local input is handed back the moment it is clear nobody is taking it.
public class SwallowedInputTests
{
    [Test]
    public async Task LocalInputComesBackWhenNothingIsConsumingIt()
    {
        var clock = new FakeClock();
        var stall = new StallingActivityTracker();
        var (platform, relay, service) = Build(stall, clock);
        try
        {
            await service.StartAsync(CancellationToken.None);
            await TransitionTestHelper.BringRemoteOnline(relay);

            platform.FireMouseMove(2559, 720);
            await service.FlushAsync();
            Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition: input is being swallowed");

            // The loop goes into a call that does not come back, with input still arriving.
            stall.Block();
            for (var i = 0; i < 10; i++) platform.FireMouseMove(1280 + i, 720);
            clock.Advance(10_000);

            await WaitUntil(() => !platform.IsOnVirtualScreen,
                "input was still being swallowed long after anything was taking it");
        }
        finally
        {
            stall.Release();
            await service.StopAsync(CancellationToken.None);
            await platform.DisposeAsync();
        }
    }

    [Test]
    public async Task ThePointerIsBroughtHomeRatherThanResumingWhereItLeftOff()
    {
        // Handing local input back is only half of it. The router still believes the pointer is on
        // the other computer, so the moment it recovers it would arm the hooks again and take the
        // keyboard straight back off the user.
        var clock = new FakeClock();
        var stall = new StallingActivityTracker();
        var (platform, relay, service) = Build(stall, clock);
        try
        {
            await service.StartAsync(CancellationToken.None);
            await TransitionTestHelper.BringRemoteOnline(relay);

            platform.FireMouseMove(2559, 720);
            await service.FlushAsync();

            stall.Block();
            for (var i = 0; i < 10; i++) platform.FireMouseMove(1280 + i, 720);
            clock.Advance(10_000);
            await WaitUntil(() => !platform.IsOnVirtualScreen, "pre-condition: local input was given back");

            stall.Release();
            await service.FlushAsync();

            relay.Sent.Clear();
            platform.ShowCursorCalled = false;
            platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'a', KeyModifiers.None));
            await service.FlushAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(platform.IsOnVirtualScreen, Is.False,
                    "the pointer is here now, so the hooks must not start swallowing again");
                Assert.That(relay.Sent.Any(m => m.Kind == MessageKind.KeyEvent), Is.False,
                    "typing has to reach this computer, not a remote screen the pointer is no longer on");
            }
        }
        finally
        {
            stall.Release();
            await service.StopAsync(CancellationToken.None);
            await platform.DisposeAsync();
        }
    }

    [Test]
    public async Task AHealthyIdleLoopIsLeftAlone()
    {
        // The watch must not mistake a quiet desk for a stuck one. Nothing is queued, so nothing is
        // being dropped, however long the pointer stays away.
        var clock = new FakeClock();
        var (platform, relay, service) = Build(new StallingActivityTracker(), clock);
        try
        {
            await service.StartAsync(CancellationToken.None);
            await TransitionTestHelper.BringRemoteOnline(relay);

            platform.FireMouseMove(2559, 720);
            await service.FlushAsync();

            clock.Advance(60_000);
            await Task.Delay(750);   // long enough for the watch to have run several times

            Assert.That(platform.IsOnVirtualScreen, Is.True,
                "an idle loop is not a stalled one -- the pointer stays where the user put it");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await platform.DisposeAsync();
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string because)
    {
        for (var waited = 0; waited < 5000 && !condition(); waited += 25)
            await Task.Delay(25);
        Assert.That(condition(), Is.True, because);
    }

    // Deliberately without FakePlatform.AfterFireCallback: these tests need to fire input while the
    // consumer is stuck, which is precisely when waiting for it would never return.
    private static (FakePlatform, FakeRelay, InputRouter) Build(IActivityTracker tracker, FakeClock clock)
    {
        var platform = new FakePlatform();
        var relay = new FakeRelay();
        var service = new InputRouter(
            platform, platform, TransitionTestHelper.TestConfig, relay, new FakeScreenDetector(),
            NullLoggerFactory.Instance, NullLogger<InputRouter>.Instance, new NullScreenSaverSync(),
            new NullClipboardSync(), FileTransferService.Null(), new NullFileSelectionDetector(),
            new NullOsdNotification(), tracker, getTickCount: clock.Now);
        return (platform, relay, service);
    }

    private sealed class FakeClock
    {
        private long _ticks = 1000;
        public long Now() => Interlocked.Read(ref _ticks);
        public void Advance(long ms) => Interlocked.Add(ref _ticks, ms);
    }

    // Stands in for any platform call that stops answering. Every input command starts by poking the
    // activity tracker, so blocking here blocks the loop exactly where a slow clipboard read would.
    private sealed class StallingActivityTracker : IActivityTracker
    {
        private volatile TaskCompletionSource? _gate;

        public void Block() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _gate?.TrySetResult();

        public long MsSinceLocalActivity => 0;

        public async ValueTask LocalActivity()
        {
            if (_gate is { } gate) await gate.Task;
        }

        public ValueTask RemoteActivity(string sourcePeer) => ValueTask.CompletedTask;
        public ValueTask IncomingPing() => ValueTask.CompletedTask;
    }
}
