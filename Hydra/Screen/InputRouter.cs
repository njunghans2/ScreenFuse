using System.Text.Json;
using System.Threading.Channels;
using Cathedral.Extensions;
using Hydra.Config;
using Hydra.FileTransfer;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Screen;

public class InputRouter(
    IPlatformInput platform,
    ICursorHider cursorHider,
    IHydraProfile profile,
    IRelaySender relay,
    IScreenDetector screens,
    ILoggerFactory loggerFactory,
    ILogger<InputRouter> log,
    IScreenSaverSync screenSaverSync,
    IClipboardSync clipboardSync,
    FileTransferService fileTransfer,
    IFileSelectionDetector selectionDetector,
    IOsdNotification osd,
    IActivityTracker activityTracker,
    IWorldState? peerState = null,
    Func<long>? getTickCount = null)
    : IHostedService
{
    private const KeyModifiers LockHotkey = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super;

    private const int MaxMouseHz = 125; // should divide evenly by 1000
    private const int MinMouseIntervalMs = 1000 / MaxMouseHz;

    // Routing belongs to the computer holding the keyboard and mouse. The router runs on every
    // computer so the role can change without restarting anything, and does nothing at all on the
    // ones that are following — exactly as if it had not been started there, which is what used to
    // happen. Read live: it changes the moment the desk hands control over.
    private bool Routing => profile.IsController;

    private readonly IWorldState _peerState = peerState ?? new WorldState();
    private readonly Func<long> _getTickCount = getTickCount ?? (() => Environment.TickCount64);

    // channel-based actor model: single consumer processes all state mutations sequentially.
    // event tap callbacks post commands via TryWrite (non-blocking); async callers use TCS.
    private readonly LocalMasterState _state = new();
    private readonly Channel<Func<LocalMasterState, ValueTask>> _commands =
        Channel.CreateUnbounded<Func<LocalMasterState, ValueTask>>(
            new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private Task? _consumerTask;

    // Talking to the clipboard, or to the file manager, means a call into another process. On
    // Windows OpenClipboard sleeps and retries while another app holds the global clipboard mutex,
    // GetClipboardData on a delayed-render format blocks until the owning app renders it, and
    // asking Explorer for the selection or for the paste folder is COM into a process that may be
    // busy. Hundreds of milliseconds is ordinary; seconds is not rare.
    //
    // None of it may run on the command consumer. That loop owns every keystroke and every mouse
    // move, and while the pointer is away the input hooks are swallowing local input on the promise
    // that the loop is forwarding it. A blocked loop turns that promise into a machine with no
    // keyboard and no mouse -- which is what a screen crossing (clipboard push) and Ctrl+C
    // (Explorer) used to do to it. They run here instead: off the loop, but still one at a time,
    // because two clipboard reads racing each other is its own bug.
    private readonly Channel<Action> _platformWork =
        Channel.CreateUnbounded<Action>(
            new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private Task? _platformWorkTask;

    // Watched from outside the consumer, so that a wedged consumer can still be caught. See CheckForStall.
    private Timer? _deadman;
    private long _lastCommandTick;
    private int _pendingInput;
    private int _swallowReleased;
    private int _parkX, _parkY;

    // How long input may be swallowed with events piling up before the loop is declared stuck.
    // Commands are sub-millisecond once the blocking platform calls are off them, so this sits far
    // above anything healthy and far below what a person will sit through.
    private const int StallReleaseMs = 2000;

    private CancellationTokenSource? _pollCts;
    private readonly IScreenSaverSync _screenSaverSync = screenSaverSync;
    private readonly IClipboardSync _clipboardSync = clipboardSync;
    private readonly FileTransferService _fileTransfer = fileTransfer;
    private readonly IFileSelectionDetector _selectionDetector = selectionDetector;
    private ClipboardSnapshot? _lastReceived;
    private string? _lastPulledFrom;

    // The return edge is checked again on a beat, not only on mouse deltas and position reports:
    // a pointer parked at a crossing — even one that never moved enough to trigger a report —
    // comes home within a beat. This is what makes the return deterministic rather than eventual.
    private Timer? _returnBeat;


    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!platform.IsAccessibilityTrusted())
        {
            log.LogWarning("Accessibility permission not granted — open System Settings › Privacy & Security › Accessibility and enable Hydra, then Hydra will continue automatically.");
            await platform.WaitForAccessibilityTrusted(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            log.LogInformation("Accessibility permission granted");
        }

        log.LogInformation("Host: {Name}", profile.Name);
        _returnBeat = new Timer(_ => CheckReturnHome(), null, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(400));

        if (!profile.RemoteOnly && profile.LocalHost == null && profile.Hosts.Count > 0)
        {
            log.LogError("Host '{Name}' is not listed in the config hosts — add it to the hosts list.", profile.Name);
            return;
        }

        var snapshot = await screens.Get(cancellationToken);

        // direct state init — safe because consumer has not started yet
        var st = _state;
        st.LocalScreens = snapshot.Screens;
        st.LocalScreenEntries = snapshot.Entries;
        st.ActiveLocalScreen = st.LocalScreens.FirstOrDefault();

        if (!profile.RemoteOnly && st.ActiveLocalScreen == null)
        {
            log.LogError("No local screens detected.");
            return;
        }

        if (st.ActiveLocalScreen != null)
            UpdateWarpPoint(st, st.ActiveLocalScreen);
        if (profile.RemoteOnly)
            st.LockedToScreen = true;  // default: locked to remote; hotkey unlocks to local
        st.Screens = BuildAllScreens(st.LocalScreens);
        st.Layout = new ScreenLayout(st.Screens, profile.Hosts, profile.DeadCorners, BuildScaleMap(st.LocalScreenEntries, []), log);

        foreach (var remote in st.Screens.Where(r => !r.IsLocal))
            log.LogInformation("Remote screen '{Name}': waiting for peer", remote.Name);

        relay.PeersChanged += OnPeersChanged;
        relay.MessageReceived += OnMessageReceived;
        relay.Disconnected += OnRelayDisconnected;
        screens.ScreensChanged += OnScreensChanged;
        profile.HostsChanged += OnHostsChanged;

        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // start consumer before event tap so early events are processed
        Volatile.Write(ref _lastCommandTick, _getTickCount());
        _consumerTask = Task.Run(ProcessCommands, cancellationToken);
        _platformWorkTask = Task.Run(ProcessPlatformWork, cancellationToken);

        await platform.StartEventTap((x, y) => OnMouseMove(x, y), OnMouseDelta, OnKeyEvent, OnMouseButton, OnMouseScroll,
            onLocalActivity: () => _ = _commands.Writer.TryWrite(_ => activityTracker.LocalActivity()));

        _screenSaverSync.ScreensaverActivated += OnScreensaverActivated;
        _screenSaverSync.ScreensaverDeactivated += OnScreensaverDeactivated;
        _screenSaverSync.ScreenLocked += OnLockDetected;
        _screenSaverSync.ScreenUnlocked += OnScreenUnlocked;
        // hideCursor belongs to the computer driving the desk; it travels in the shared document
        // like everything else, so a follower would otherwise hide its own cursor on its own screen
        if (profile.HideCursor && Routing)
            cursorHider.Hide();

        profile.ControllerChanged += OnControllerChanged;

        _deadman = new Timer(_ => CheckForStall(), null, StallReleaseMs / 4, StallReleaseMs / 4);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _returnBeat?.Dispose();
        _deadman?.Dispose();
        _deadman = null;
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        relay.PeersChanged -= OnPeersChanged;
        relay.MessageReceived -= OnMessageReceived;
        relay.Disconnected -= OnRelayDisconnected;
        screens.ScreensChanged -= OnScreensChanged;
        profile.HostsChanged -= OnHostsChanged;

        _screenSaverSync.ScreensaverActivated -= OnScreensaverActivated;
        _screenSaverSync.ScreensaverDeactivated -= OnScreensaverDeactivated;
        _screenSaverSync.ScreenLocked -= OnLockDetected;
        _screenSaverSync.ScreenUnlocked -= OnScreenUnlocked;
        profile.ControllerChanged -= OnControllerChanged;
        platform.StopEventTap();

        // drain remaining commands, then stop consumer
        _commands.Writer.TryComplete();
        if (_consumerTask != null)
            await _consumerTask;

        _platformWork.Writer.TryComplete();
        if (_platformWorkTask != null)
            await _platformWorkTask;

        // consumer is done; safe to access _state directly
        if (_state.Mouse.IsOnVirtualScreen)
        {
            platform.IsOnVirtualScreen = false;
            cursorHider.Show();
        }
    }

    // Control moved. Nothing is created or destroyed here: both halves have been running all
    // along, and this is the moment they change which one is in charge.
    private void OnControllerChanged()
    {
        if (Routing)
        {
            // Taking over. The peers have to be told who to answer to now — they learn it from
            // MasterConfig, which until this moment came from somebody else.
            log.LogInformation("This computer now has the keyboard and mouse");
            // Whoever was the controller a moment ago is filed here as this machine's master, from
            // the MasterConfig it sent while it still was one. Left there, this computer would keep
            // forwarding its logs to a machine that is now following it.
            _ = _peerState.PruneMasters([]);
            var payload = MessageSerializer.Encode(MessageKind.MasterConfig, new MasterConfigMessage(profile.LogLevel));
            foreach (var host in profile.RemoteHosts.Select(h => h.Name))
                relay.Send([host], payload);
            _ = RebuildForNewCrossingsAsync();
            return;
        }

        // Giving it up. If the pointer is standing on another computer's screen it has to come
        // home first: the local input hooks are swallowing everything on the promise that this
        // router is forwarding it, and a router that has just stopped forwarding would leave this
        // machine with no keyboard and no mouse at all.
        log.LogInformation("The keyboard and mouse moved to {Host}", profile.Controller ?? "another computer");
        _ = _commands.Writer.TryWrite(async st =>
        {
            if (st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
            {
                var host = LeaveVirtualScreen(st, out var warpX, out var warpY);
                if (host != null)
                {
                    ReturnToLocalScreen(warpX, warpY);
                    ShowCursorOnReturn();
                }
            }
            platform.IsOnVirtualScreen = false;
            cursorHider.Show();
            await ValueTask.CompletedTask;
        });
    }

    private async Task ProcessCommands()
    {
        await foreach (var cmd in _commands.Reader.ReadAllAsync())
        {
            // guard EACH command: a throw from one command must not kill the consumer, which would
            // silently wedge all edge-crossing / relay-message routing until process restart.
            try
            {
                await cmd(_state);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "InputRouter command failed — continuing");
            }
            finally
            {
                // Answered after every command, including the one that threw. See ReconcileSwallow.
                ReconcileSwallow(_state);
                Volatile.Write(ref _lastCommandTick, _getTickCount());
            }
        }
    }

    // Runs the calls that reach out to the clipboard and the file manager, off the command consumer
    // and one at a time. Nothing here reports back: every caller has already decided what it wants
    // and none of them needs an answer, which is exactly why this work never belonged on the loop
    // that routes input.
    private async Task ProcessPlatformWork()
    {
        await foreach (var work in _platformWork.Reader.ReadAllAsync())
        {
            // Every exception, cancellation included. There is no shutdown signal to read here --
            // the queue closing is the only one -- so letting anything escape would end the worker
            // and take clipboard sync with it for the rest of the session, in silence.
            try { work(); }
            catch (Exception ex)
            {
                log.LogError(ex, "Clipboard or file-manager work failed — continuing");
            }
        }
    }

    private void OffLoop(Action work) => _platformWork.Writer.TryWrite(work);

    // Every key, click, scroll and mouse movement is queued through here, so that the watch below
    // can tell input that is waiting to be forwarded from a loop that simply has nothing to do. The
    // count comes down even when the command throws: either way the event is no longer waiting.
    private void PostInput(Func<LocalMasterState, ValueTask> body)
    {
        Interlocked.Increment(ref _pendingInput);
        if (!_commands.Writer.TryWrite(async st =>
            {
                try { await body(st); }
                finally { Interlocked.Decrement(ref _pendingInput); }
            }))
            Interlocked.Decrement(ref _pendingInput);
    }

    // The input hooks swallow every local key and click while platform.IsOnVirtualScreen is set, on
    // the promise that the pointer is away and this router is forwarding them. The router's own
    // answer to that question is st.Mouse.IsOnVirtualScreen. The half-dozen places that arm and
    // clear the flag still do so where they do, because the moment matters -- swallowing has to
    // start before the cursor is parked and stop before it is put back. What they cannot do is
    // guarantee the pair ends up agreeing, on a consumer that deliberately survives a throw: a
    // command that threw between arming the flag and entering the screen left a machine with no
    // keyboard and no mouse and nothing that would ever clear it, because the router believed the
    // pointer was already home. So the flag is settled here, after every command, from the one
    // piece of state that knows the answer.
    private void ReconcileSwallow(LocalMasterState st)
    {
        // The deadman gave local input back because this loop had stopped answering. Whatever the
        // state still claims, the pointer is here now -- bring it home rather than resume swallowing.
        if (Interlocked.Exchange(ref _swallowReleased, 0) == 1 && st.Mouse.IsOnVirtualScreen)
        {
            var host = LeaveVirtualScreen(st, out var homeX, out var homeY);
            if (host != null)
            {
                ReturnToLocalScreen(homeX, homeY);
                ShowCursorOnReturn();
                if (relay.IsConnected) LeaveRemoteScreen(host);
                log.LogWarning("The pointer was brought home after input routing stalled on '{Host}'", host);
            }
        }

        var shouldSwallow = st.Mouse.IsOnVirtualScreen;
        if (platform.IsOnVirtualScreen == shouldSwallow) return;
        if (!shouldSwallow)
            log.LogWarning("Local input was being swallowed with the pointer at home — released");
        platform.IsOnVirtualScreen = shouldSwallow;
    }

    // The loop that forwards input can stop answering -- a platform call that blocks, a deadlock, a
    // bug -- and while it does, the hooks keep swallowing every key and click on its behalf. That is
    // a machine with no keyboard and no mouse, and nothing inside the loop can rescue it, because
    // the loop is the thing that stopped. So it is watched from out here, where a stall is visible:
    // input being swallowed, events piling up, and none of them coming off the queue. Local input
    // goes back to the user immediately; ReconcileSwallow brings the pointer home when the loop
    // recovers, so that it does not simply start swallowing again.
    private void CheckForStall()
    {
        if (!platform.IsOnVirtualScreen) return;
        var waiting = Volatile.Read(ref _pendingInput);
        if (waiting == 0) return;   // nothing waiting is an idle loop, not a stuck one
        var stalledMs = _getTickCount() - Volatile.Read(ref _lastCommandTick);
        if (stalledMs < StallReleaseMs) return;
        if (Interlocked.Exchange(ref _swallowReleased, 1) == 1) return;   // already given back

        log.LogError("Input has been swallowed for {Ms}ms with {Waiting} event(s) waiting — giving local input back",
            stalledMs, waiting);
        platform.IsOnVirtualScreen = false;
        cursorHider.Show();
        platform.WarpCursor(Volatile.Read(ref _parkX), Volatile.Read(ref _parkY));
    }

    // posts a fence command and awaits it. the tcs is ALWAYS completed — even if the command throws
    // (logged) or the channel is closed — so a fence awaiter (screen/peer/disconnect handlers) can never
    // hang, the counterpart to ProcessCommands' per-command guard.
    private Task RunFence(Func<LocalMasterState, ValueTask> body)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(async st =>
            {
                try { await body(st); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "InputRouter fence command failed"); }
                finally { tcs.TrySetResult(); }
            }))
            tcs.TrySetResult();
        return tcs.Task;
    }

    private Task<T> RunFence<T>(Func<LocalMasterState, T> body, T onFailure)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(st =>
            {
                try { tcs.TrySetResult(body(st)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "InputRouter fence command failed"); }
                finally { tcs.TrySetResult(onFailure); } // no-op if body already set a value; guarantees completion on any throw (incl. OCE)
                return ValueTask.CompletedTask;
            }))
            tcs.TrySetResult(onFailure);
        return tcs.Task;
    }

    // posts a fence command and awaits it — all previously queued commands will have been processed on return.
    // used by tests to synchronize after firing platform events.
    //
    // Both queues, because the work is split across both: a command hands the clipboard and the file
    // manager to the platform worker, and the worker hands the result back as a command. Draining
    // commands alone would return before any of that had happened.
    internal async Task FlushAsync()
    {
        if (_consumerTask == null) return;
        await FlushCommands();
        await FlushPlatformWork();
        await FlushCommands();
    }

    private Task FlushCommands()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(_ => { tcs.TrySetResult(); return ValueTask.CompletedTask; }))
            return Task.CompletedTask;
        return tcs.Task;
    }

    private Task FlushPlatformWork()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_platformWork.Writer.TryWrite(() => tcs.TrySetResult()))
            return Task.CompletedTask;
        return tcs.Task;
    }

    // The desk was rearranged. The crossings live in Hosts and the layout is derived from them, so
    // it has to be derived again — the pointer uses the layout, not the config, and nothing else
    // here would ever ask for it. Dragging a monitor changes no screen and moves no peer, which is
    // exactly why the two rebuild triggers below could not cover this.
    private void OnHostsChanged() => _ = RebuildForNewCrossingsAsync();

    private async Task RebuildForNewCrossingsAsync()
    {
        try
        {
            var peerScreens = await _peerState.GetPeerScreensSnapshot();
            await RunFence(st =>
            {
                RebuildLayout(st, peerScreens);
                return ValueTask.CompletedTask;
            });
            log.LogInformation("Desk crossings changed — pointer layout rebuilt");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Failed to rebuild the pointer layout after the desk changed");
        }
    }

    // A display appeared or disappeared. This is the path a monitor changing hands takes -- the
    // desk hands it to the other computer, this one loses the signal and says so -- and it used to
    // build the layout itself, inline. That made it the one rebuild that skipped what RebuildLayout
    // does at the end: bring the pointer home when the new layout leaves it nowhere to go, follow
    // the screen it is standing on, and put the park point back on a display that still exists. A
    // desk switch fires this and OnHostsChanged together and this one usually writes last, so the
    // guard the other path ran was overwritten by a layout that had never been checked.
    private async Task OnScreensChanged(LocalScreenSnapshot snapshot)
    {
        log.LogInformation("Screen configuration changed — rebuilding layout");
        LogDetectedScreens(snapshot.Screens);
        var peerScreens = await _peerState.GetPeerScreensSnapshot();

        await RunFence(st =>
        {
            st.LocalScreens = snapshot.Screens;
            st.LocalScreenEntries = snapshot.Entries;
            RebuildLayout(st, peerScreens);
            return ValueTask.CompletedTask;
        });
    }

    private async Task OnPeersChanged(string[] hostNames)
    {
        var current = new HashSet<string>(hostNames, StringComparer.OrdinalIgnoreCase);
        var configuredSlaves = profile.RemoteHosts
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var delta = await _peerState.UpdatePeers(current, configuredSlaves);

        var (disconnectedHost, warpX, warpY) = await RunFence<(string?, int, int)>(st =>
        {
            string? host = null;
            int wx = 0, wy = 0;

            if (st.Mouse.CurrentScreen != null && !current.Contains(st.Mouse.CurrentScreen.Host))
                host = LeaveVirtualScreen(st, out wx, out wy);

            if (delta.AnyDeparted) RebuildLayout(st, delta.PeerScreensSnapshot);
            return (host, wx, wy);
        }, (null, 0, 0));

        if (disconnectedHost != null)
        {
            ReturnToLocalScreen(warpX, warpY);
            ShowCursorOnReturn();
            log.LogInformation("Remote peer '{Name}' disconnected — returned to local screen", disconnectedHost);
        }

        // abort a transfer only if ITS peer actually left — not merely because the cursor's current screen
        // peer did (Abort tears down ANY transfer, so the old cursor-screen-coupled call aborted unrelated ones)
        _fileTransfer.AbortIfPeerGone(current, relay);

        // send MasterConfig only to newly appeared peers, and only while this computer is the one
        // holding the keyboard — it is the message that tells a peer who to treat as the controller
        if (Routing)
            foreach (var host in delta.NewPeers)
            {
                var payload = MessageSerializer.Encode(MessageKind.MasterConfig, new MasterConfigMessage(profile.LogLevel));
                relay.Send([host], payload);
                log.LogDebug("Sent MasterConfig to {Host}", host);
            }

        if (profile.RemoteOnly)
        {
            await RunFence(async st => await TryEnterRemoteOnly(st));
        }
    }

    private async Task OnRelayDisconnected()
    {
        var (disconnectedHost, warpX, warpY) = await RunFence<(string?, int, int)>(st =>
        {
            var host = LeaveVirtualScreen(st, out var wx, out var wy);
            return (host, wx, wy);
        }, (null, 0, 0));

        // reset known peers so all slaves get a fresh MasterConfig on reconnect
        await _peerState.ClearPeers();

        if (disconnectedHost != null)
        {
            _fileTransfer.Abort(relay, "relay disconnected");
            ReturnToLocalScreen(warpX, warpY);
            ShowCursorOnReturn();
            log.LogWarning("Relay disconnected — returned to local screen from '{Host}'", disconnectedHost);
        }
    }

    private void OnScreensaverActivated()
    {
        if (!Routing) return;  // the controller syncs the screensaver; the others follow it
        _ = _commands.Writer.TryWrite(async st =>
        {
            if (st.ScreensaverActive) return;
            st.ScreensaverActive = true;

            if (st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
            {
                st.SavedScreenName = st.Mouse.CurrentScreen.Name;
                st.SavedCursorX = (int)st.Mouse.X;
                st.SavedCursorY = (int)st.Mouse.Y;
                FlushMouseDelta(st);
                var disconnectedHost = LeaveVirtualScreen(st, out var warpX, out var warpY);
                if (disconnectedHost != null)
                {
                    _fileTransfer.Abort(relay, "screensaver activated");
                    LeaveRemoteScreen(disconnectedHost);
                    ReturnToLocalScreen(warpX, warpY);
                    ShowCursorOnReturn();
                }
            }

            await BroadcastScreensaverSync(true);
            log.LogInformation("Screensaver activated — synced to slaves");
        });
    }

    private void OnScreensaverDeactivated()
    {
        _ = _commands.Writer.TryWrite(async st =>
        {
            if (!st.ScreensaverActive) return;
            st.ScreensaverActive = false;
            var savedScreen = st.SavedScreenName;
            var savedX = st.SavedCursorX;
            var savedY = st.SavedCursorY;
            st.SavedScreenName = null;

            await BroadcastScreensaverSync(false);
            log.LogInformation("Screensaver deactivated — synced to slaves");

            // best-effort cursor restore: re-enter saved remote screen if still connected and accessible
            if (savedScreen != null && relay.IsConnected)
            {
                var dest = st.Screens.FirstOrDefault(sc => !sc.IsLocal && sc.Name.EqualsIgnoreCase(savedScreen));
                if (dest != null && dest.Width > 0)
                {
                    var peerScreens = await _peerState.GetPeerScreensSnapshot();
                    var remoteInfo = GetRemoteScreensAndScales(st.Screens, peerScreens, dest);
                    cursorHider.Hide();
                    platform.IsOnVirtualScreen = true;
                    ApplyEnterScreen(st, dest, remoteInfo, savedX, savedY);
                    // anchor immediately (bogus filter); delay physical warp until the shield is absorbing
                    st.LastWarpX = st.WarpX;
                    st.LastWarpY = st.WarpY;
                    await platform.WarpToPark(st.WarpX, st.WarpY);
                    SendEnterScreen(dest, savedX, savedY);
                    log.LogInformation("Restored cursor to '{Screen}' after screensaver", savedScreen);
                }
            }
        });
    }

    private async ValueTask BroadcastScreensaverSync(bool active)
    {
        var peerScreens = await _peerState.GetPeerScreensSnapshot();
        var hosts = peerScreens.Keys.ToArray();
        if (hosts.Length == 0) return;
        var payload = MessageSerializer.Encode(MessageKind.ScreensaverSync, new ScreensaverSyncMessage(active));
        relay.Send(hosts, payload);
    }

    private void OnLockDetected()
    {
        if (!Routing || !profile.ScreenLockPropagation) return;
        _ = _commands.Writer.TryWrite(async st =>
        {
            log.LogInformation("Machine locked — propagating to slaves");
            await BroadcastLockScreen(st);
        });
    }

    private void OnScreenUnlocked()
    {
        _ = _commands.Writer.TryWrite(async _ =>
        {
            log.LogInformation("Screen unlocked — restarting event tap");
            await platform.RestartEventTap();
        });
    }

    private async ValueTask BroadcastLockScreen(LocalMasterState st)
    {
        var peerScreens = await _peerState.GetPeerScreensSnapshot();
        var hosts = peerScreens.Keys.ToArray();
        if (hosts.Length == 0) return;
        var msSinceInput = _getTickCount() - st.LastInputTick;
        var payload = MessageSerializer.Encode(MessageKind.LockScreen, new LockScreenMessage(msSinceInput));
        relay.Send(hosts, payload);
    }

    // sends master's clipboard hash to slave; slave decides whether to request the full push.
    // reads the clipboard off the consumer -- this runs on every screen crossing, and on Windows a
    // clipboard read can block for as long as the app that owns the clipboard takes to answer.
    private void PushClipboardToHost(string host) => OffLoop(() =>
    {
        try
        {
            var clip = ClipboardUtils.ReadWithFallback(_clipboardSync, _lastReceived, log, "push");
            if (string.IsNullOrEmpty(clip.Text) && string.IsNullOrEmpty(clip.PrimaryText) && clip.ImagePng == null && clip.Html == null && clip.Rtf == null)
                return; // nothing to push
            var hash = ClipboardUtils.ClipboardHash(clip);
            log.LogDebug("Sending clipboard hash to {Host}", host);
            relay.Send([host], MessageSerializer.Encode(MessageKind.ClipboardHash, new ClipboardHashMessage(hash)));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to send clipboard hash to {Host}", host);
        }
    });

    // slave has compared hashes and determined it needs our clipboard; send the full push
    private void OnClipboardPullRequest(string host) => OffLoop(() =>
    {
        try
        {
            var clip = ClipboardUtils.ReadWithFallback(_clipboardSync, _lastReceived, log, "push");
            if (string.IsNullOrEmpty(clip.Text) && string.IsNullOrEmpty(clip.PrimaryText) && clip.ImagePng == null && clip.Html == null && clip.Rtf == null)
                return;
            relay.Send([host], MessageSerializer.Encode(MessageKind.ClipboardPush, new ClipboardPushMessage(clip.Text ?? "", clip.PrimaryText, clip.ImagePng, clip.Html, clip.Rtf)));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to push clipboard to {Host}", host);
        }
    });

    // also off the consumer, and for the same reason: this runs on every crossing back.
    private void PullClipboardFromHost(string host) => OffLoop(() =>
    {
        log.LogDebug("Pulling clipboard from {Host}", host);
        _lastPulledFrom = host;
        var localClip = ClipboardUtils.ReadWithFallback(_clipboardSync, _lastReceived, log, "pull");
        var masterHash = ClipboardUtils.ClipboardHash(localClip);
        relay.Send([host], MessageSerializer.Encode(MessageKind.ClipboardPull, new ClipboardPullMessage(masterHash)));
    });

    private async Task OnMessageReceived(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        switch (kind)
        {
            case MessageKind.ScreenInfo:
                var info = body.ParseMessage<ScreenInfoMessage>(log, $"ScreenInfo from {sourceHost}");
                if (info != null && info.Screens.Count > 0)
                {
                    await _peerState.SetPeerScreens(sourceHost, info.Screens);
                    if (info.Platform.HasValue)
                        await _peerState.SetPeerPlatform(sourceHost, info.Platform.Value);
                    var snapshot = await _peerState.GetPeerScreensSnapshot();
                    await RunFence(async st =>
                    {
                        RebuildLayout(st, snapshot);
                        if (profile.RemoteOnly) await TryEnterRemoteOnly(st);
                    });
                    log.LogInformation("Screen info from {Host}: {Count} screen(s)", sourceHost, info.Screens.Count);
                }
                break;
            case MessageKind.SlaveLog:
                var entry = body.ParseMessage<SlaveLogMessage>(log, $"SlaveLog from {sourceHost}");
                if (entry != null) ForwardSlaveLog(sourceHost, entry);
                break;
            case MessageKind.CursorPosition:
                ReconcileCursor(sourceHost, body);
                break;
            case MessageKind.ScreensaverSync:
                break; // master never acts on screensaver sync messages
            case MessageKind.ActivityPing:
                _ = _commands.Writer.TryWrite(_ => activityTracker.RemoteActivity(sourceHost));
                break;
            case MessageKind.ClipboardPullRequest:
                {
                    // only honour if cursor is currently on that slave's screen
                    var onThatScreen = await RunFence(
                        st => st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen?.Host.EqualsIgnoreCase(sourceHost) == true,
                        false);
                    if (onThatScreen)
                        OnClipboardPullRequest(sourceHost);
                    else
                        log.LogDebug("Clipboard pull request from {Host} ignored (cursor not on that screen)", sourceHost);
                    break;
                }
            case MessageKind.ClipboardPullResponse:
                var clip = body.ParseMessage<ClipboardPullResponseMessage>(log, $"ClipboardPullResponse from {sourceHost}");
                if (clip != null)
                {
                    // only apply if from the slave we last pulled from
                    if (!sourceHost.EqualsIgnoreCase(_lastPulledFrom))
                    {
                        log.LogDebug("Clipboard pull response from {Host} ignored (not last pulled from)", sourceHost);
                        break;
                    }
                    if (clip.Unchanged == true)
                    {
                        log.LogDebug("Clipboard pull response from {Host}: unchanged", sourceHost);
                        break;
                    }
                    log.LogDebug("Clipboard pull response from {Host}: text={TextLen}, primary={PrimaryLen}, image={ImageLen}",
                        sourceHost, clip.Text?.Length, clip.PrimaryText?.Length, clip.ImagePng?.Length);
                    var validated = ClipboardUtils.ValidateFields(clip.Text, clip.PrimaryText, clip.ImagePng, clip.Html, clip.Rtf, log, "pull response", sourceHost);
                    OffLoop(() => _clipboardSync.SetClipboard(validated));
                    // if cursor is currently on a remote screen, forward the clipboard to it
                    var activeHost = await RunFence(st =>
                    {
                        _lastReceived = validated;
                        return st.Mouse.CurrentScreen?.Host;
                    }, null);
                    if (activeHost != null)
                        PushClipboardToHost(activeHost);
                }
                break;
            case MessageKind.FileSelectionResponse:
                {
                    var osdText = _fileTransfer.HandleSelectionResponse(sourceHost, body);
                    var osdPayload = MessageSerializer.Encode(MessageKind.Osd, new OsdMessage(osdText));
                    relay.Send([sourceHost], osdPayload);
                    break;
                }
            case MessageKind.FileTransferBusy:
                {
                    _fileTransfer.HandleBusy(sourceHost);
                    _ = _commands.Writer.TryWrite(st => { ShowOsd(st, "Transfer in progress"); return ValueTask.CompletedTask; });
                    break;
                }
            case var _ when FileTransferService.IsFileTransferMessage(kind):
                {
                    var wasSendingTo = _fileTransfer.IsSendingTo(sourceHost);
                    var wasCoordinating = _fileTransfer.IsCoordinatingTransferTo(sourceHost);
                    var wasReceivingFrom = _fileTransfer.IsReceivingFrom(sourceHost);
                    if (wasSendingTo || wasCoordinating)
                    {
                        if (kind == MessageKind.FileTransferAccepted)
                            SendOsd(sourceHost, "Pasted!");
                        else if (kind == MessageKind.FileTransferAbort)
                        {
                            var abort = body.FromSaneJson<FileTransferAbortMessage>();
                            if (abort?.Reason == FileTransferService.ReasonNoFolder)
                                SendOsd(sourceHost, "Invalid paste target");
                        }
                    }
                    await _fileTransfer.OnMessageAsync(sourceHost, kind, body, relay);
                    if (wasReceivingFrom && kind == MessageKind.FileTransferDone)
                        osd.Show("Pasted!");
                    break;
                }
            default:
                log.LogDebug("Unhandled message kind {Kind} from {Host}", kind, sourceHost);
                break;
        }
    }

    // routes OSD to slave when cursor is remote, otherwise shows locally on master
    private void ShowOsd(LocalMasterState st, string message)
    {
        if (st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
        {
            var payload = MessageSerializer.Encode(MessageKind.Osd, new OsdMessage(message));
            relay.Send([st.Mouse.CurrentScreen.Host], payload);
        }
        else
            osd.Show(message);
    }

    // sends OSD to a specific known host (file transfer outcomes, etc.)
    private void SendOsd(string targetHost, string message)
    {
        if (targetHost.EqualsIgnoreCase(profile.Name))
            osd.Show(message);
        else
        {
            var payload = MessageSerializer.Encode(MessageKind.Osd, new OsdMessage(message));
            relay.Send([targetHost], payload);
        }
    }

    private void ForwardSlaveLog(string sourceHost, SlaveLogMessage entry)
    {
        var category = $"slave:{sourceHost}/{entry.Category}";
        var logger = _peerState.GetOrCreateSlaveLogger(category, loggerFactory);

        var level = (LogLevel)entry.Level;
        // ReSharper disable TemplateIsNotCompileTimeConstantProblem
        if (entry.Exception != null)
            logger.Log(level, "{Message}\n{Exception}", entry.Message, entry.Exception);
        else
            logger.Log(level, "{Message}", entry.Message);
        // ReSharper restore TemplateIsNotCompileTimeConstantProblem
    }

    // rebuilds screens/layout from localScreens/peerScreens; must be called from consumer
    private void RebuildLayout(LocalMasterState st, Dictionary<string, List<ScreenInfoEntry>> peerScreens)
    {
        if (!profile.RemoteOnly && st.ActiveLocalScreen == null) return;

        var newScreens = BuildAllScreens(st.LocalScreens);
        ApplyPeerScreenSizes(peerScreens, newScreens);
        var newLayout = new ScreenLayout(newScreens, profile.Hosts, profile.DeadCorners, BuildScaleMap(st.LocalScreenEntries, peerScreens), log);
        st.Screens = newScreens;
        st.Layout = newLayout;
        st.ActiveLocalScreen = st.ActiveLocalScreen == null ? null
            : st.LocalScreens.FirstOrDefault(s => s.Name.EqualsIgnoreCase(st.ActiveLocalScreen.Name)) ?? st.LocalScreens.FirstOrDefault() ?? st.ActiveLocalScreen;

        // prune stale relative-mode entries for screens that no longer exist
        var validNames = new HashSet<string>(st.Screens.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var key in st.RelativeMouseScreens.Keys.Where(k => !validNames.Contains(k)).ToList())
            st.RelativeMouseScreens.Remove(key);

        // The park point is the middle of a local screen, and it is where the pointer is physically
        // held while it is away on another computer. A monitor changing hands takes that screen with
        // it, and warping to the middle of a display that no longer exists lands the cursor wherever
        // the desktop clamps it to -- after which every movement is measured against an anchor the
        // cursor is not sitting on, comes out as an impossible jump, and is discarded as bogus. The
        // mouse goes dead while still being swallowed. So the park point is re-derived here, and if
        // the pointer is away the cursor is moved onto it, so that anchor and cursor still agree.
        if (st.ActiveLocalScreen != null)
        {
            var (parkedX, parkedY) = (st.WarpX, st.WarpY);
            UpdateWarpPoint(st, st.ActiveLocalScreen);
            if (st.Mouse.IsOnVirtualScreen && (st.WarpX != parkedX || st.WarpY != parkedY))
            {
                platform.WarpCursor(st.WarpX, st.WarpY);
                st.LastWarpX = st.WarpX;
                st.LastWarpY = st.WarpY;
            }
        }

        // The pointer is out on another computer's screen and the layout it relied on to get back has
        // just been replaced. If the screen is gone, or the new layout gives it no edges, it is
        // stranded — and this computer hid its cursor when the pointer left, so it is invisible as
        // well as unreachable, with no input that can recover it. Bring it home instead.
        if (st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
        {
            var landing = st.Screens.FirstOrDefault(s => s.Name.EqualsIgnoreCase(st.Mouse.CurrentScreen.Name));
            if (landing == null || !newLayout.HasAnyExit(landing))
            {
                var left = LeaveVirtualScreen(st, out var homeX, out var homeY);
                if (left != null)
                {
                    log.LogWarning(
                        "The pointer was on {Host} with no way back after the desk changed — brought it home", left);
                    ReturnToLocalScreen(homeX, homeY);
                    ShowCursorOnReturn();
                }
            }
        }

        // if the cursor is on a remote screen whose dims changed, update it
        if (st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
        {
            var refreshed = st.Screens.FirstOrDefault(s => s.Name.EqualsIgnoreCase(st.Mouse.CurrentScreen.Name));
            if (refreshed != null && refreshed != st.Mouse.CurrentScreen)
            {
                var remoteInfo = GetRemoteScreensAndScales(st.Screens, peerScreens, refreshed);
                st.Mouse.EnterScreen(refreshed, remoteInfo.Screens, (int)st.Mouse.X, (int)st.Mouse.Y,
                    remoteInfo.ScaleMap.GetValueOrDefault(refreshed.Name, 1.0m), remoteInfo.ScaleMap, remoteInfo.RelativeScaleMap);
            }
        }
    }

    private static Dictionary<string, decimal> BuildScaleMap(
        List<ScreenInfoEntry> localEntries, Dictionary<string, List<ScreenInfoEntry>> peerScreens)
    {
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in localEntries)
            map[e.Name] = e.MouseScale;
        foreach (var entries in peerScreens.Values)
            foreach (var e in entries)
                map[e.Name] = e.MouseScale;
        return map;
    }

    // replaces per-host placeholders with actual per-screen entries from ScreenInfo; must be called from consumer
    private static void ApplyPeerScreenSizes(Dictionary<string, List<ScreenInfoEntry>> peerScreens, List<ScreenRect> screens)
    {
        for (var i = screens.Count - 1; i >= 0; i--)
        {
            var screen = screens[i];
            if (!screen.IsLocal && peerScreens.TryGetValue(screen.Host, out var entries))
            {
                screens.RemoveAt(i);
                // insert in reverse so final order matches ScreenInfo order
                for (var j = entries.Count - 1; j >= 0; j--)
                {
                    var e = entries[j];
                    // Carry the identity across. A crossing names the screen it arrives on, and that
                    // name is one the remote computer uses — so without this the destination
                    // resolves to nothing and the crossing is dropped without a word.
                    screens.Insert(i, new ScreenRect(e.Name, screen.Host, e.X, e.Y, e.Width, e.Height, IsLocal: false,
                        new ScreenIdentity
                        {
                            ScreenName = e.Name,
                            Output = e.Output,
                            DisplayName = e.DisplayName,
                            PlatformId = e.PlatformId,
                        }));
                }
            }
        }
    }

    private static ScreenInfoEntry? FindRemoteEntry(Dictionary<string, List<ScreenInfoEntry>> peerScreens, ScreenRect screen)
    {
        if (peerScreens.TryGetValue(screen.Host, out var entries))
            return entries.FirstOrDefault(e => e.Name.EqualsIgnoreCase(screen.Name));
        return null;
    }

    // builds remoteScreens list + scaleMaps for a given destination host; used when entering a remote screen
    private static RemoteScreenInfo GetRemoteScreensAndScales(
        List<ScreenRect> allScreens, Dictionary<string, List<ScreenInfoEntry>> peerScreens, ScreenRect target)
    {
        var screens = allScreens.Where(s => !s.IsLocal && s.Host.EqualsIgnoreCase(target.Host)).ToList();
        var scaleMap = screens.ToDictionary(s => s.Name, s => FindRemoteEntry(peerScreens, s)?.MouseScale ?? 1.0m, StringComparer.OrdinalIgnoreCase);
        var relativeScaleMap = screens.ToDictionary(s => s.Name, s => FindRemoteEntry(peerScreens, s)?.RelativeMouseScale, StringComparer.OrdinalIgnoreCase);
        return new RemoteScreenInfo(screens, scaleMap, relativeScaleMap);
    }

    private void OnKeyEvent(KeyEvent keyEvent)
    {
        // Not the controller: every key is this machine's own. Not forwarded, not swallowed by a
        // hotkey, and not read for a file copy the user meant for their own file manager.
        if (!Routing) return;

        var label = keyEvent.Character.HasValue ? $" '{keyEvent.Character}'" : keyEvent.Key.HasValue ? $" {keyEvent.Key}" : "";

        PostInput(async st =>
        {
            st.LastInputTick = _getTickCount();
            await activityTracker.LocalActivity();
            if (st.Mouse.IsOnVirtualScreen)
                log.LogDebug("Key: {Type}{Label} mods={Modifiers}", keyEvent.Type, label, keyEvent.Modifiers);

            // Consume only ScreenFuse control shortcuts. Copy and paste deliberately use
            // the platform-standard shortcuts so the focused file manager remains the
            // source/destination the user expects.
            var hotkeyConsumed = (keyEvent.Modifiers & LockHotkey) == LockHotkey && keyEvent.Character is 'l' or 'm' or 'z' or 'k';
            var filePasteConsumed = false;
            if (!hotkeyConsumed && keyEvent.Type == KeyEventType.KeyDown && !keyEvent.IsRepeat)
            {
                if (IsStandardClipboardShortcut(keyEvent, 'c'))
                    CaptureFocusedFileCopy(st);
                else if (IsStandardClipboardShortcut(keyEvent, 'v'))
                    filePasteConsumed = PasteFocusedFileCopy(st);
            }
            // !IsRepeat: an auto-repeat of a held hotkey must not re-fire the toggle every tick
            if (hotkeyConsumed && keyEvent.Type == KeyEventType.KeyDown && !keyEvent.IsRepeat)
            {
                if (keyEvent.Character == 'l')
                {
                    // A remote-only master with no local screen has nowhere to pass input to: the
                    // old behaviour left the remote screen and OnMouseDelta then dropped every
                    // delta, so keyboard and mouse appeared dead until the hotkey was pressed
                    // again. Confine the cursor to the current remote screen instead, which is what
                    // the hotkey does in every other mode.
                    if (profile.RemoteOnly && !st.Screens.Any(sc => sc.IsLocal))
                    {
                        st.ConfinedToScreen = !st.ConfinedToScreen;
                        ShowOsd(st, st.ConfinedToScreen ? "Cursor lock: On" : "Cursor lock: Off");
                        log.LogInformation("Cursor lock: {State} (remote-only, no local screen)",
                            st.ConfinedToScreen ? "confined to current screen" : "free to roam");
                    }
                    else
                    {
                        st.LockedToScreen = !st.LockedToScreen;
                        if (profile.RemoteOnly)
                        {
                            // ShowOsd falls back to a LOCAL osd when the cursor is not on a remote
                            // screen, and a remote-only master often has no display to show it on. So
                            // each branch shows its OSD at the only moment a remote screen is current:
                            // after entering when locking, before leaving when unlocking. Announcing it
                            // up front instead made re-locking silent.
                            if (st.LockedToScreen)
                            {
                                log.LogInformation("Remote lock: locked to remote");
                                await TryEnterRemoteOnly(st);
                                ShowOsd(st, "Input: remote");
                            }
                            else
                            {
                                log.LogInformation("Remote lock: unlocked (local)");
                                if (st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
                                {
                                    var leavingHost = st.Mouse.CurrentScreen.Host;
                                    ShowOsd(st, "Input: local");
                                    FlushMouseDelta(st);
                                    st.Mouse.LeaveScreen();
                                    platform.IsOnVirtualScreen = false;
                                    ShowCursorOnReturn();
                                    LeaveRemoteScreen(leavingHost);
                                }
                            }
                        }
                        else
                        {
                            ShowOsd(st, st.LockedToScreen ? "Mouse lock: On" : "Mouse lock: Off");
                            log.LogInformation("Screen lock: {State}", st.LockedToScreen ? "locked" : "unlocked");
                        }
                    }
                }
                else if (keyEvent.Character == 'k')
                {
                    // Lock every connected slave on demand. On a remote-only master this is the only
                    // route to BroadcastLockScreen: that is otherwise driven by this machine's own
                    // ScreenLocked event, which is Mac/Windows-only and cannot fire on a headless box.
                    // Deliberately not gated on screenLockPropagation - that flag governs automatic
                    // propagation, and an explicit keypress should never silently do nothing.
                    ShowOsd(st, "Locking slaves");
                    log.LogInformation("Lock hotkey: locking all slaves");
                    await BroadcastLockScreen(st);
                }
                else if (keyEvent.Character == 'm' && st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
                {
                    var screenName = st.Mouse.CurrentScreen.Name;
                    var isNowRelative = !st.RelativeMouseScreens.GetValueOrDefault(screenName);
                    st.RelativeMouseScreens[screenName] = isNowRelative;
                    log.LogInformation("Mouse mode for '{Screen}': {Mode}", screenName, isNowRelative ? "relative" : "absolute");
                    ShowOsd(st, isNowRelative ? "Relative mouse: On" : "Relative mouse: Off");
                }
                else if (keyEvent.Character == 'c')
                {
                    if (_fileTransfer.FileTransferOngoing)
                    {
                        ShowOsd(st, "Transfer in progress");
                    }
                    else if (!st.Mouse.IsOnVirtualScreen)
                    {
                        if (!_selectionDetector.IsFileTransferSupported)
                        {
                            log.LogInformation("Copy hotkey: file transfer not supported on this platform");
                            ShowOsd(st, "Action not supported");
                        }
                        else
                        {
                            var result = _selectionDetector.GetSelectedPaths();
                            if (!result.FileManagerFocused)
                            {
                                log.LogInformation("Copy hotkey: {Name} is not focused", _selectionDetector.FileManagerName);
                                ShowOsd(st, $"{_selectionDetector.FileManagerName} is not focused");
                            }
                            else if (result.Paths != null)
                            {
                                log.LogInformation("Copy hotkey: {Count} file(s) selected locally: {Paths}", result.Paths.Count, string.Join(", ", result.Paths));
                                _fileTransfer.SetCopyBuffer(profile.Name, result.Paths);
                                var n = result.Paths.Count;
                                ShowOsd(st, $"{n} {(n == 1 ? "item" : "items")} copied");
                            }
                            else
                            {
                                log.LogInformation("Copy hotkey: no files selected locally");
                                _fileTransfer.ClearCopyBuffer();
                                ShowOsd(st, "0 items selected");
                            }
                        }
                    }
                    else if (st.Mouse.CurrentScreen != null && relay.IsConnected)
                    {
                        log.LogInformation("Copy hotkey: querying file selection on {Host}", st.Mouse.CurrentScreen.Host);
                        var queryPayload = MessageSerializer.Encode(MessageKind.FileSelectionQuery, new FileSelectionQueryMessage());
                        relay.Send([st.Mouse.CurrentScreen.Host], queryPayload);
                    }
                }
                else if (keyEvent.Character == 'z' && st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null)
                {
                    log.LogInformation("Mission Control hotkey: sending to {Host}", st.Mouse.CurrentScreen.Host);
                    var host = st.Mouse.CurrentScreen.Host;
                    relay.Send([host], MessageSerializer.Encode(MessageKind.KeyEvent, new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, null, SpecialKey.MissionControl)));
                    relay.Send([host], MessageSerializer.Encode(MessageKind.KeyEvent, new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, null, SpecialKey.MissionControl)));
                }
                else if (keyEvent.Character == 'v')
                {
                    if (_fileTransfer.FileTransferOngoing)
                    {
                        ShowOsd(st, "Transfer in progress");
                    }
                    else if (!_selectionDetector.IsFileTransferSupported)
                    {
                        log.LogInformation("Paste hotkey: file transfer not supported on this platform");
                        ShowOsd(st, "Action not supported");
                    }
                    else if (_fileTransfer.GetCopyBuffer() is { } copyBuffer)
                    {
                        var targetHost = st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null
                            ? st.Mouse.CurrentScreen.Host
                            : profile.Name;
                        if (string.Equals(copyBuffer.SourceHost, targetHost, StringComparison.OrdinalIgnoreCase))
                        {
                            log.LogInformation("Paste hotkey: source and target are the same host ({Host}), nothing to do", targetHost);
                            ShowOsd(st, "Invalid paste target");
                        }
                        else
                        {
                            log.LogInformation("Paste hotkey: {Count} file(s) from {Source} → {Target}", copyBuffer.Paths.Length, copyBuffer.SourceHost, targetHost);
                            if (!_fileTransfer.InitiatePaste(copyBuffer, targetHost, profile.Name, relay))
                                SendOsd(targetHost, "Invalid paste target");
                        }
                    }
                    else
                    {
                        log.LogInformation("Paste hotkey: copy buffer is empty");
                        ShowOsd(st, "Nothing to paste");
                    }
                }
            }

            if (!hotkeyConsumed && !filePasteConsumed && st.Mouse.IsOnVirtualScreen && relay.IsConnected)
            {
                // repeats are master-driven: each OS auto-repeat is re-resolved (live modifier/dead-key state)
                // and forwarded with IsRepeat set, so the slave injects the correct character every tick.
                ForwardToVirtualScreen(st, MessageKind.KeyEvent, new KeyEventMessage(keyEvent.Type, keyEvent.Modifiers, keyEvent.Character, RemapKey(keyEvent.Key), IsRepeat: keyEvent.IsRepeat, UnicodeKeyRepeat: profile.UnicodeKeyRepeat));
            }
        });
    }

    private static bool IsStandardClipboardShortcut(KeyEvent keyEvent, char character) =>
        char.ToLowerInvariant(keyEvent.Character ?? '\0') == character
        && (keyEvent.Modifiers & (OperatingSystem.IsMacOS() ? KeyModifiers.Super : KeyModifiers.Control)) != 0;

    private void CaptureFocusedFileCopy(LocalMasterState st)
    {
        if (_fileTransfer.FileTransferOngoing) return;
        if (st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null && relay.IsConnected)
        {
            // Let the ordinary copy command continue to the remote application while
            // asking its focused file manager for the selected paths.
            relay.Send([st.Mouse.CurrentScreen.Host], MessageSerializer.Encode(
                MessageKind.FileSelectionQuery, new FileSelectionQueryMessage()));
            return;
        }

        if (!_selectionDetector.IsFileTransferSupported) return;

        // Asking the file manager what is selected is COM into Explorer, and Explorer answers when
        // it feels like it. Doing that here used to hold up every queued keystroke and mouse move
        // behind one Ctrl+C -- which, with the pointer away and the hooks swallowing on this loop's
        // behalf, is how a copy could take the keyboard with it.
        OffLoop(() =>
        {
            var result = _selectionDetector.GetSelectedPaths();
            if (!result.FileManagerFocused || result.Paths is not { Count: > 0 })
            {
                // Copying text must never cause a stale file selection to hijack Paste.
                _fileTransfer.ClearCopyBuffer();
                return;
            }

            _fileTransfer.SetCopyBuffer(profile.Name, result.Paths);
            var n = result.Paths.Count;
            _ = _commands.Writer.TryWrite(inner =>
            {
                ShowOsd(inner, $"{n} {(n == 1 ? "item" : "items")} copied");
                return ValueTask.CompletedTask;
            });
        });
    }

    private bool PasteFocusedFileCopy(LocalMasterState st)
    {
        if (_fileTransfer.FileTransferOngoing || !_selectionDetector.IsFileTransferSupported) return false;
        if (_fileTransfer.GetCopyBuffer() is not { } copyBuffer) return false;

        var targetHost = st.Mouse.IsOnVirtualScreen && st.Mouse.CurrentScreen != null
            ? st.Mouse.CurrentScreen.Host
            : profile.Name;
        if (string.Equals(copyBuffer.SourceHost, targetHost, StringComparison.OrdinalIgnoreCase)) return false;

        log.LogInformation("Paste: {Count} file(s) from {Source} → focused computer {Target}",
            copyBuffer.Paths.Length, copyBuffer.SourceHost, targetHost);
        // Starting the paste asks the file manager where it is pointed, which is the same blocking
        // call into Explorer that Ctrl+C makes. The decision above is cheap and stays here, because
        // the caller needs it now to know whether to also pass Ctrl+V on to the remote; only the
        // slow half goes off the loop. Its answer never fed that decision -- it only picks an OSD.
        OffLoop(() =>
        {
            if (!_fileTransfer.InitiatePaste(copyBuffer, targetHost, profile.Name, relay))
                SendOsd(targetHost, "Open a folder before pasting files");
        });
        return true;
    }

    private void OnMouseButton(MouseButtonEvent e)
    {
        if (!Routing) return;
        PostInput(async st =>
        {
            st.LastInputTick = _getTickCount();
            await activityTracker.LocalActivity();
            if (st.Mouse.IsOnVirtualScreen && relay.IsConnected)
            {
                log.LogDebug("Mouse: {Type} {Button}", e.IsPressed ? "down" : "up", e.Button);
                ForwardToVirtualScreen(st, MessageKind.MouseButton, new MouseButtonMessage(e.Button, e.IsPressed));
            }
        });
    }

    private void OnMouseScroll(MouseScrollEvent e)
    {
        if (!Routing) return;
        PostInput(async st =>
        {
            st.LastInputTick = _getTickCount();
            await activityTracker.LocalActivity();
            if (st.Mouse.IsOnVirtualScreen && relay.IsConnected)
            {
                log.LogDebug("Scroll: x={X} y={Y}", e.XDelta, e.YDelta);
                ForwardToVirtualScreen(st, MessageKind.MouseScroll, new MouseScrollMessage(e.XDelta, e.YDelta));
            }
        });
    }

    private void SendMousePosition(LocalMasterState st, long now)
    {
        if (!relay.IsConnected || st.Mouse.CurrentScreen == null) return;

        var screen = st.Mouse.CurrentScreen;
        byte[] payload;

        if (st.RelativeMouseScreens.GetValueOrDefault(screen.Name))
        {
            // relative mode: send accumulated delta, preserve sub-pixel remainders
            var intDx = (int)st.PendingDx;
            var intDy = (int)st.PendingDy;
            if (intDx == 0 && intDy == 0) return;
            st.PendingDx -= intDx;
            st.PendingDy -= intDy;
            if (profile.DebugMouse)
                log.LogInformation("[mouse] delta to {Host}: dx={Dx} dy={Dy}", screen.Host, intDx, intDy);
            payload = MessageSerializer.Encode(MessageKind.MouseMoveDelta, new MouseMoveDeltaMessage(intDx, intDy));
        }
        else
        {
            // absolute mode: send current virtual position, discard accumulated deltas
            st.PendingDx = 0;
            st.PendingDy = 0;
            if (profile.DebugMouse)
                log.LogInformation("[mouse] move to {Host}: screen={Screen} x={X} y={Y}", screen.Host, screen.Name, (int)st.Mouse.X, (int)st.Mouse.Y);
            payload = MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage(screen.Name, (int)st.Mouse.X, (int)st.Mouse.Y));
        }

        st.LastMouseSendTick = now;
        relay.Send([screen.Host], payload);
    }

    // flush pending state before leaving a virtual screen.
    // relative mode: send any accumulated delta.
    // absolute mode: always send current position so slave cursor doesn't lag at exit point.
    private void FlushMouseDelta(LocalMasterState st)
    {
        if (!relay.IsConnected || st.Mouse.CurrentScreen == null) return;
        var screen = st.Mouse.CurrentScreen;
        var isRelative = st.RelativeMouseScreens.GetValueOrDefault(screen.Name);
        if (isRelative && st.PendingDx == 0 && st.PendingDy == 0) return;
        SendMousePosition(st, _getTickCount());
    }

    // remap Home/End to platform-independent line-nav keys when master is not Mac.
    private static SpecialKey? RemapKey(SpecialKey? key) => key switch
    {
        SpecialKey.Home when !OperatingSystem.IsMacOS() => SpecialKey.MoveToBeginningOfLine,
        SpecialKey.End when !OperatingSystem.IsMacOS() => SpecialKey.MoveToEndOfLine,
        _ => key,
    };

    private void ForwardToVirtualScreen<T>(LocalMasterState st, MessageKind kind, T message)
    {
        var target = st.Mouse.CurrentScreen?.Host;
        if (target == null) return;
        var payload = MessageSerializer.Encode(kind, message);
        relay.Send([target], payload);
    }

    private void OnMouseMove(double x, double y)
    {
        if (!Routing) return;
        PostInput(async st =>
        {
            st.LastInputTick = _getTickCount();
            await activityTracker.LocalActivity();
            if (st.Layout is null || st.ActiveLocalScreen is null) return;
            if (!st.Mouse.IsOnVirtualScreen)
                await HandleRealScreenMove(st, x, y);
            else
                await HandleVirtualScreenMove(st, x, y);
        });
    }

    private async ValueTask HandleRealScreenMove(LocalMasterState st, double x, double y)
    {
        // track which local screen the cursor is on
        var screen = FindLocalScreenAt(st, (int)x, (int)y) ?? st.ActiveLocalScreen!;
        if (screen != st.ActiveLocalScreen)
        {
            st.ActiveLocalScreen = screen;
            UpdateWarpPoint(st, screen);
        }

        if (st.LockedToScreen) return;

        var localX = (int)x - screen.X;
        var localY = (int)y - screen.Y;
        var hit = st.Layout!.DetectEdgeExit(screen, localX, localY);
        if (hit is null) return;
        if (!relay.IsConnected) return;

        var peerScreens = await _peerState.GetPeerScreensSnapshot();
        var remoteInfo = GetRemoteScreensAndScales(st.Screens, peerScreens, hit.Destination);

        // block edge crossing while any button is held
        if (platform.AnyMouseButtonHeld()) return;

        cursorHider.Hide();
        platform.IsOnVirtualScreen = true;
        ApplyEnterScreen(st, hit.Destination, remoteInfo, hit.EntryX, hit.EntryY);
        // set the warp anchor immediately so pre-queued events compute large dx → caught by bogus filter,
        // but delay the physical warp until the shield is actually absorbing (avoids hover at the park point)
        st.LastWarpX = st.WarpX;
        st.LastWarpY = st.WarpY;
        await platform.WarpToPark(st.WarpX, st.WarpY);
        // The pointer is on its way to the park; the event that captures it arriving is not a user
        // movement, so it is re-anchored rather than applied. Every movement after that is the
        // user's, and none is dropped — a dropped movement is exactly what strands the pointer on a
        // remote screen with no way back to the edge it came from.
        st.SuppressNextMove = true;
        log.LogInformation("Entered remote screen '{Name}' → ({X}, {Y})", hit.Destination.Name, hit.EntryX, hit.EntryY);
        SendEnterScreen(hit.Destination, hit.EntryX, hit.EntryY);
    }

    private async ValueTask HandleVirtualScreenMove(LocalMasterState st, double x, double y)
    {
        var dx = x - st.LastWarpX;
        var dy = y - st.LastWarpY;

        // zero-delta filter (also catches Mac/Windows warp events which arrive at WarpX,WarpY)
        if (dx == 0 && dy == 0) return;

        // The event right after the entry warp is usually the pointer arriving at the park, not a user
        // movement — if it is a big jump (the park was clamped to a different spot), re-anchor to
        // wherever it actually landed and ignore it. A small first delta is the user's own nudge
        // and is applied like any other: no size filter, because dropping a fast flick is what
        // strands the pointer on a remote screen with no reliable way back.
        if (st.SuppressNextMove)
        {
            st.SuppressNextMove = false;
            if (Math.Abs(dx) > st.HalfW - 10 || Math.Abs(dy) > st.HalfH - 10)
            {
                st.LastWarpX = x;
                st.LastWarpY = y;
                return;
            }
        }

        var prevScreen = st.Mouse.ApplyDelta(dx, dy);
        if (prevScreen != null)
            HandleIntraHostTransition(st);
        else
        {
            // same screen — accumulate scaled deltas for throttle
            var isRelative = st.RelativeMouseScreens.GetValueOrDefault(st.Mouse.CurrentScreen!.Name);
            var scale = isRelative ? (double)(st.Mouse.RelativeMouseScale ?? st.Mouse.MouseScale) : (double)st.Mouse.MouseScale;
            st.PendingDx += dx * scale;
            st.PendingDy += dy * scale;
        }

        var now = _getTickCount();
        if (now - st.LastVirtualLogTick >= 100)
        {
            st.LastVirtualLogTick = now;
            if (st.Mouse.CurrentScreen != null && st.RelativeMouseScreens.GetValueOrDefault(st.Mouse.CurrentScreen.Name))
                log.LogDebug("Mouse: ({X}, {Y})  Offset: ({DX}, {DY})", (int)st.Mouse.X, (int)st.Mouse.Y, (int)st.PendingDx, (int)st.PendingDy);
            else
                log.LogDebug("Mouse: ({X}, {Y})", (int)st.Mouse.X, (int)st.Mouse.Y);
        }

        // check edge exit
        {
            var virtualScreen = st.Mouse.CurrentScreen!;
            var hit = st.Layout!.DetectEdgeExit(virtualScreen, (int)st.Mouse.X, (int)st.Mouse.Y);
            if (hit is not null)
            {
                if (!hit.Destination.IsLocal)
                {
                    if (profile.RemoteOnly ? !st.ConfinedToScreen : !st.LockedToScreen)
                    {
                        if (!platform.AnyMouseButtonHeld())
                        {
                            var leavingScreen = st.Mouse.CurrentScreen;
                            FlushMouseDelta(st);
                            var peerScreens = await _peerState.GetPeerScreensSnapshot();
                            var remoteInfo = GetRemoteScreensAndScales(st.Screens, peerScreens, hit.Destination);
                            ApplyEnterScreen(st, hit.Destination, remoteInfo, hit.EntryX, hit.EntryY);
                            log.LogInformation("Switched to remote screen '{Name}' → ({X}, {Y})", hit.Destination.Name, hit.EntryX, hit.EntryY);

                            if (relay.IsConnected)
                            {
                                if (leavingScreen != null && leavingScreen.Host != hit.Destination.Host)
                                    LeaveRemoteScreen(leavingScreen.Host);
                                SendEnterScreen(hit.Destination, hit.EntryX, hit.EntryY);
                            }
                            return;
                        }
                    }
                }
                else if (!st.LockedToScreen && !profile.RemoteOnly)
                {
                    if (!platform.AnyMouseButtonHeld())
                    {
                        var targetScreen = hit.Destination;

                        FlushMouseDelta(st);

                        var globalX = targetScreen.X + hit.EntryX;
                        var globalY = targetScreen.Y + hit.EntryY;
                        var leavingScreen = st.Mouse.CurrentScreen;
                        st.Mouse.LeaveScreen();
                        ReturnToLocalScreen(globalX, globalY);
                        ShowCursorOnReturn();
                        st.ActiveLocalScreen = targetScreen;
                        UpdateWarpPoint(st, targetScreen);
                        log.LogInformation("Returned to local screen ← ({X}, {Y})", globalX, globalY);

                        if (relay.IsConnected && leavingScreen != null)
                            LeaveRemoteScreen(leavingScreen.Host);
                        return;
                    }
                }
            }
        }

        // throttle mouse sends to MaxMouseHz
        if (now - st.LastMouseSendTick >= MinMouseIntervalMs)
            SendMousePosition(st, now);

        // warp to center on every event; LastWarpX/Y anchored to warp center so Mac/Windows
        // synthetic warp events compute dx=0 and are dropped by the zero-delta filter above
        platform.WarpCursor(st.WarpX, st.WarpY);
        st.LastWarpX = st.WarpX;
        st.LastWarpY = st.WarpY;
    }

    private void HandleIntraHostTransition(LocalMasterState st)
    {
        st.PendingDx = 0;
        st.PendingDy = 0;
        if (!relay.IsConnected || st.Mouse.CurrentScreen == null) return;
        var s = st.Mouse.CurrentScreen;
        var payload = MessageSerializer.Encode(MessageKind.EnterScreen,
            new EnterScreenMessage(s.Name, (int)st.Mouse.X, (int)st.Mouse.Y, s.Width, s.Height));
        relay.Send([s.Host], payload);
    }

    private void ReturnToLocalScreen(int x, int y)
    {
        platform.IsOnVirtualScreen = false;
        platform.WarpCursor(x, y);
    }

    // hides or shows the cursor when control returns to the local screen:
    // with hideCursor, restart the inactivity timer instead of showing unconditionally
    private void ShowCursorOnReturn()
    {
        if (profile.HideCursor) cursorHider.Hide();
        else cursorHider.Show();
    }

    // The slave reports where its pointer actually is, whenever it is not where the master placed
    // it (the machine's own trackpad moved it, an app grabbed it). Snap the virtual pointer to
    // reality, then check the return edge immediately — a pointer parked at a crossing that leads
    // home comes straight back, instead of waiting for a delta that may never line up.
    private void ReconcileCursor(string sourceHost, ReadOnlyMemory<byte> body)
    {
        var msg = body.ParseMessage<CursorPositionMessage>(log, $"CursorPosition from {sourceHost}");
        if (msg == null) return;
        _ = RunFence(st =>
        {
            if (!st.Mouse.IsOnVirtualScreen || st.Mouse.CurrentScreen == null) return ValueTask.CompletedTask;
            if (!st.Mouse.CurrentScreen.Host.Equals(sourceHost, StringComparison.OrdinalIgnoreCase)) return ValueTask.CompletedTask;
            if (!st.Mouse.CurrentScreen.Name.EqualsIgnoreCase(msg.Screen)) return ValueTask.CompletedTask;

            st.Mouse.SetPosition(msg.X, msg.Y);

            if (st.Layout?.DetectEdgeExit(st.Mouse.CurrentScreen, (int)st.Mouse.X, (int)st.Mouse.Y) is { } hit)
                TryReturnViaEdge(st, hit, $"reconciled from {sourceHost}");
            return ValueTask.CompletedTask;
        });
    }

    // The beat: every ~400ms, while the pointer is on a remote screen, re-check the position we
    // hold — kept fresh by the slave's periodic reports — against the return edges. A pointer the
    // user parked at a crossing comes home even if no delta and no drift report ever fired.
    private void CheckReturnHome()
    {
        _ = RunFence(st =>
        {
            if (!st.Mouse.IsOnVirtualScreen || st.Mouse.CurrentScreen == null) return ValueTask.CompletedTask;
            if (platform.AnyMouseButtonHeld()) return ValueTask.CompletedTask;
            if (st.Layout?.DetectEdgeExit(st.Mouse.CurrentScreen, (int)st.Mouse.X, (int)st.Mouse.Y) is { } hit)
                TryReturnViaEdge(st, hit, "interval check");
            return ValueTask.CompletedTask;
        });
    }

    // Shared by the delta path, the position reconcile and the interval beat: leave a remote screen
    // through the crossing the pointer sits on, and land on the local screen at the crossing's
    // entry point. A button being held is the one case that must not cross — the user is dragging.
    private void TryReturnViaEdge(LocalMasterState st, EdgeHit hit, string via)
    {
        if (!hit.Destination.IsLocal || st.LockedToScreen || platform.AnyMouseButtonHeld()) return;
        var targetScreen = hit.Destination;
        FlushMouseDelta(st);
        var globalX = targetScreen.X + hit.EntryX;
        var globalY = targetScreen.Y + hit.EntryY;
        var leavingScreen = st.Mouse.CurrentScreen;
        st.Mouse.LeaveScreen();
        ReturnToLocalScreen(globalX, globalY);
        ShowCursorOnReturn();
        st.ActiveLocalScreen = targetScreen;
        UpdateWarpPoint(st, targetScreen);
        log.LogInformation("Returned to local screen ← ({X}, {Y}) ({Via})", globalX, globalY, via);
        if (relay.IsConnected && leavingScreen != null)
            LeaveRemoteScreen(leavingScreen.Host);
    }

    // Troubleshooting: restore this computer's cursor unconditionally. If the pointer is stranded
    // on a remote screen (its cursor is hidden here), bring it home first.
    public void ResetCursorState()
    {
        // The OS cursor can be left blank by a previous instance that died mid-crossing — the
        // system cursor is replaced (Windows) or ref-counted-hidden (macOS), and no owner remains
        // to put it back. Force-restore regardless of what this process believes.
        try
        {
            if (OperatingSystem.IsWindows()) Hydra.Platform.Windows.WindowsCursorSnapshot.RestoreDefaults();
            if (OperatingSystem.IsMacOS()) Hydra.Platform.MacOs.NativeMethods.CGDisplayShowCursor(Hydra.Platform.MacOs.NativeMethods.CGMainDisplayID());
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not force-restore the OS cursor"); }
        _ = RunFence(st =>
        {
            var left = LeaveVirtualScreen(st, out var homeX, out var homeY);
            if (left != null)
            {
                ReturnToLocalScreen(homeX, homeY);
                log.LogWarning("Cursor reset: the pointer was on {Host} — brought it home", left);
            }
            cursorHider.Show();
            return ValueTask.CompletedTask;
        });
    }

    // shared in-consumer cleanup for peer-disconnect / relay-disconnect / screensaver snap-back.
    // returns the host we left (null if already on local screen).
    private static string? LeaveVirtualScreen(LocalMasterState st, out int warpX, out int warpY)
    {
        warpX = warpY = 0;
        if (!st.Mouse.IsOnVirtualScreen || st.Mouse.CurrentScreen == null) return null;
        var host = st.Mouse.CurrentScreen.Host;
        st.Mouse.LeaveScreen();
        st.PendingDx = 0;
        st.PendingDy = 0;
        warpX = st.WarpX;
        warpY = st.WarpY;
        return host;
    }

    // enters remote-only mode targeting the first remote screen with known dimensions.
    // must be called from consumer. no-op if already on virtual screen or no screen ready yet.
    private async ValueTask TryEnterRemoteOnly(LocalMasterState st)
    {
        if (st.Mouse.IsOnVirtualScreen) return;
        if (!st.LockedToScreen) return;  // user explicitly unlocked to local — don't auto-re-enter
        var target = st.Screens.FirstOrDefault(s => !s.IsLocal && s.Width > 0);
        if (target == null) return;
        if (!relay.IsConnected) return;

        var peerScreens = await _peerState.GetPeerScreensSnapshot();
        var remoteInfo = GetRemoteScreensAndScales(st.Screens, peerScreens, target);
        var entryX = target.Width / 2;
        var entryY = target.Height / 2;

        cursorHider.Hide();
        platform.IsOnVirtualScreen = true;
        ApplyEnterScreen(st, target, remoteInfo, entryX, entryY);
        st.LockedToScreen = true;
        log.LogInformation("Remote-only: entered '{Name}' → ({X}, {Y})", target.Name, entryX, entryY);
        SendEnterScreen(target, entryX, entryY);
    }

    // handles raw mouse deltas — used by evdev (remote-only) and Xorg (local master, virtual screen).
    // feeds directly into VirtualMouseState — no warp-point math needed.
    private void OnMouseDelta(double dx, double dy)
    {
        if (!Routing) return;
        PostInput(async st =>
        {
            st.LastInputTick = _getTickCount();
            await activityTracker.LocalActivity();
            if (!st.Mouse.IsOnVirtualScreen) return;

            var leavingScreen = st.Mouse.CurrentScreen!;
            var prevScreen = st.Mouse.ApplyDelta(dx, dy);
            if (prevScreen != null)
            {
                HandleIntraHostTransition(st);
                if (st.ActiveLocalScreen != null) platform.WarpCursor(st.WarpX, st.WarpY);
                return;
            }

            var hit = st.Layout?.DetectEdgeExit(st.Mouse.CurrentScreen!, (int)st.Mouse.X, (int)st.Mouse.Y);
            if (hit is not null)
            {
                if (!hit.Destination.IsLocal)
                {
                    // Cursor lock in remote-only mode: refuse to leave this machine. Falling through
                    // rather than returning lets the normal send path run, and ApplyDelta has already
                    // clamped the position, so the cursor simply stops against the edge.
                    // Confinement is per host: moving between a slave's own monitors is unaffected,
                    // since that happens inside ApplyDelta as an intra-host transition.
                    if (profile.RemoteOnly && st.ConfinedToScreen)
                    {
                        log.LogDebug("Cursor lock: blocked transition to '{Name}'", hit.Destination.Name);
                    }
                    else if (relay.IsConnected && !platform.AnyMouseButtonHeld())
                    {
                        await HandleEvdevCrossHostTransitionAsync(st, leavingScreen, hit);
                        return;
                    }
                }
                else if (!st.LockedToScreen && !profile.RemoteOnly && !platform.AnyMouseButtonHeld())
                {
                    var targetScreen = hit.Destination;
                    FlushMouseDelta(st);
                    var globalX = targetScreen.X + hit.EntryX;
                    var globalY = targetScreen.Y + hit.EntryY;
                    st.Mouse.LeaveScreen();
                    ReturnToLocalScreen(globalX, globalY);
                    ShowCursorOnReturn();
                    st.ActiveLocalScreen = targetScreen;
                    UpdateWarpPoint(st, targetScreen);
                    log.LogInformation("Returned to local screen ← ({X}, {Y})", globalX, globalY);
                    if (relay.IsConnected)
                        LeaveRemoteScreen(leavingScreen.Host);
                    return;
                }
            }

            var isRelative = st.RelativeMouseScreens.GetValueOrDefault(st.Mouse.CurrentScreen!.Name);
            var scale = isRelative ? (double)(st.Mouse.RelativeMouseScale ?? st.Mouse.MouseScale) : (double)st.Mouse.MouseScale;
            st.PendingDx += dx * scale;
            st.PendingDy += dy * scale;

            var now = _getTickCount();
            if (now - st.LastMouseSendTick >= MinMouseIntervalMs)
                SendMousePosition(st, now);

            // warp to keep cursor near center — prevents it hitting local screen edges while on virtual
            if (st.ActiveLocalScreen != null) platform.WarpCursor(st.WarpX, st.WarpY);
        });
    }

    // evdev cross-host transitions; called from consumer, so st access is safe
    private async ValueTask HandleEvdevCrossHostTransitionAsync(LocalMasterState st, ScreenRect leavingScreen, EdgeHit hit)
    {
        FlushMouseDelta(st);
        var peerScreens = await _peerState.GetPeerScreensSnapshot();
        var remoteInfo = GetRemoteScreensAndScales(st.Screens, peerScreens, hit.Destination);
        ApplyEnterScreen(st, hit.Destination, remoteInfo, hit.EntryX, hit.EntryY);
        log.LogInformation("Switched to remote screen '{Name}' → ({X}, {Y})", hit.Destination.Name, hit.EntryX, hit.EntryY);
        if (leavingScreen.Host != hit.Destination.Host)
            LeaveRemoteScreen(leavingScreen.Host);
        SendEnterScreen(hit.Destination, hit.EntryX, hit.EntryY);
    }

    // updates mouse state when entering a remote screen; always called before sending relay messages
    private static void ApplyEnterScreen(LocalMasterState st, ScreenRect dest, RemoteScreenInfo remoteInfo, int entryX, int entryY)
    {
        var scale = remoteInfo.ScaleMap.GetValueOrDefault(dest.Name, 1.0m);
        st.Mouse.EnterScreen(dest, remoteInfo.Screens, entryX, entryY, scale, remoteInfo.ScaleMap, remoteInfo.RelativeScaleMap);
        st.PendingDx = 0;
        st.PendingDy = 0;
    }

    // sends EnterScreen relay message + pushes clipboard to destination host
    private void SendEnterScreen(ScreenRect dest, int entryX, int entryY)
    {
        var payload = MessageSerializer.Encode(MessageKind.EnterScreen, new EnterScreenMessage(dest.Name, entryX, entryY, dest.Width, dest.Height));
        relay.Send([dest.Host], payload);
        PushClipboardToHost(dest.Host);
    }

    // sends LeaveScreen relay message + pulls clipboard from host (unconditional — callers guard the condition)
    private void LeaveRemoteScreen(string host)
    {
        relay.Send([host], MessageSerializer.Encode(MessageKind.LeaveScreen, new LeaveScreenMessage()));
        PullClipboardFromHost(host);
    }

    private void UpdateWarpPoint(LocalMasterState st, ScreenRect screen)
    {
        st.HalfW = screen.Width / 2;
        st.HalfH = screen.Height / 2;
        st.WarpX = screen.X + st.HalfW;
        st.WarpY = screen.Y + st.HalfH;
        // published for CheckForStall, which runs off the consumer and so cannot read st
        Volatile.Write(ref _parkX, st.WarpX);
        Volatile.Write(ref _parkY, st.WarpY);
        cursorHider.UpdateWarpPoint(st.WarpX, st.WarpY);
    }

    private static ScreenRect? FindLocalScreenAt(LocalMasterState st, int x, int y) =>
        st.LocalScreens.FirstOrDefault(s => s.Contains(x, y));

    private void LogDetectedScreens(List<ScreenRect> detected)
    {
        log.LogInformation("Detected {Count} local screen(s):", detected.Count);
        for (var i = 0; i < detected.Count; i++)
            if (detected[i].Identity != null)
                log.LogInformation("  Screen {I}: {Json}", i, JsonSerializer.Serialize(detected[i].Identity, ScreenDetector.JsonOptions));
    }

    // combines local screens with placeholder remote screens (Width=0 until ScreenInfo arrives)
    private List<ScreenRect> BuildAllScreens(List<ScreenRect> localScreens)
    {
        var result = new List<ScreenRect>(localScreens);
        foreach (var host in profile.RemoteHosts)
            result.Add(new ScreenRect(host.Name, host.Name, 0, 0, 0, 0, IsLocal: false));
        return result;
    }

    private class LocalMasterState
    {
        public List<ScreenRect> Screens = [];
        public List<ScreenRect> LocalScreens = [];
        public List<ScreenInfoEntry> LocalScreenEntries = [];
        public ScreenRect? ActiveLocalScreen;
        public ScreenLayout? Layout;
        public VirtualMouseState Mouse = new();
        public int WarpX, WarpY, HalfW, HalfH;
        public double LastWarpX, LastWarpY;
        // true right after the entry warp: the pointer is still arriving at the park point, so the
        // next captured position is not a user movement — re-anchor to it instead of applying it
        public bool SuppressNextMove;
        public long LastVirtualLogTick;
        public bool LockedToScreen;
        // remote-only with no local screen: confine the cursor to the current remote screen.
        // separate from LockedToScreen, which in remote-only means 'input is on remote at all'
        // and defaults to true - reusing it would confine the cursor by default.
        public bool ConfinedToScreen;

        // per-screen relative mouse mode (true = relative, false/absent = absolute)
        public Dictionary<string, bool> RelativeMouseScreens = new(StringComparer.OrdinalIgnoreCase);

        // screensaver sync: saved cursor location before screensaver snap-back
        public bool ScreensaverActive;
        public string? SavedScreenName;
        public int SavedCursorX, SavedCursorY;

        // 120Hz throttle: accumulated deltas and last send time
        public long LastMouseSendTick;
        public double PendingDx;
        public double PendingDy;

        // last time any input event was processed, regardless of destination (local or remote)
        public long LastInputTick;
    }

    private record RemoteScreenInfo(List<ScreenRect> Screens, Dictionary<string, decimal> ScaleMap, Dictionary<string, decimal?> RelativeScaleMap);
}
