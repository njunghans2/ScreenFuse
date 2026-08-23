using System.Collections.Concurrent;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Display;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Scenes;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Desk;

// The desk, as every computer sees it.
//
// One computer is the controller: it merges what the others report, owns the desk document, and
// broadcasts the merged picture. Every computer runs this service and shows the same settings
// window; a computer that is not the controller forwards the user's actions instead of performing
// them, so the desk behaves identically no matter which machine you happen to be sitting at.
public sealed class DeskService : SimpleHostedService, IDeskService
{
    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(12);

    private readonly IHydraProfile _profile;
    private readonly IScreenDetector _screens;
    private readonly IDisplayRouter _router;
    private readonly IRelaySender _relay;
    private readonly IWorldState _world;
    private readonly DeskConfigStore _store;
    private readonly ControllerOverrideStore _controllerStore;
    private readonly ISceneCoordinator _scenes;
    private readonly ILogger<DeskService> _log;
    private readonly Action _restart;
    private readonly TimeSpan _restartDelay;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, DeskInventoryMessage> _reports = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DeskSetInputResultMessage>> _pending = new();
    private readonly Dictionary<string, string> _optimistic = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _replied = new(StringComparer.OrdinalIgnoreCase);

    private HydraConfigFile _config;
    private string[] _peers = [];
    private string _controller;
    private DeskSnapshot _snapshot;
    private int _restartScheduled;

    public DeskService(
        IHydraProfile profile,
        IScreenDetector screens,
        IDisplayRouter router,
        IRelaySender relay,
        IWorldState world,
        DeskConfigStore store,
        ControllerOverrideStore controllerStore,
        ISceneCoordinator scenes,
        ILogger<DeskService> log,
        Action? restart = null,
        TimeSpan? restartDelay = null)
        : base(log, loopTime: TimeSpan.FromSeconds(4))
    {
        _profile = profile;
        _screens = screens;
        _router = router;
        _relay = relay;
        _world = world;
        _store = store;
        _controllerStore = controllerStore;
        _scenes = scenes;
        _log = log;
        _restart = restart ?? (() => ProcessRestart.Restart("desk settings or control changed"));
        _restartDelay = restartDelay ?? TimeSpan.FromMilliseconds(750);

        _config = SafeLoad();
        _controller = profile.Mode == Mode.Master ? profile.Name : profile.Controller ?? "";
        _snapshot = DeskSnapshot.Empty(profile.Name);

        _relay.MessageReceived += OnMessageReceived;
        _relay.PeersChanged += OnPeersChanged;
    }

    public DeskSnapshot Snapshot => _snapshot;
    public event Action? Changed;

    private bool IsController => _profile.Mode == Mode.Master;
    private string LocalName => _profile.Name;

    protected override Task OnShutdown(CancellationToken cancel)
    {
        _relay.MessageReceived -= OnMessageReceived;
        _relay.PeersChanged -= OnPeersChanged;
        return Task.CompletedTask;
    }

    // Runs exactly one round. Lets a test drive two desks against each other without waiting out
    // the loop interval — the two-computer handshake is where the mistakes have actually been.
    internal Task PumpAsync(CancellationToken cancel = default) => Execute(cancel);

    protected override async Task Execute(CancellationToken cancelToken)
    {
        var cancel = cancelToken;
        // Said out loud, once. A round that never finishes leaves the desk on whatever it last had
        // -- which at startup is nothing at all -- and the only visible symptom is a desk reporting
        // no monitors, which reads as "not set up yet" rather than "this is failing every round".
        // A round that never returns wedges the desk forever, and silently: the loop never ticks
        // again, the snapshot stays on whatever it last had -- at startup, nothing -- and the desk
        // reports no monitors, which reads as "not set up yet" rather than "stuck".
        using var round = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        round.CancelAfter(TimeSpan.FromSeconds(30));
        cancel = round.Token;

        try
        {
            _log.LogDebug("Desk round: refreshing peers");
            await RefreshPeersAsync();
            _log.LogDebug("Desk round: reading monitors");
            var inventory = await BuildInventoryAsync(cancel);
            _log.LogDebug("Desk round: {Count} monitor(s) readable here", inventory.Monitors?.Count ?? 0);
            if (IsController)
            {
                _reports[LocalName] = inventory;
                await RecomputeAsync(cancel);
                if (!_roundCompleted)
                {
                    _roundCompleted = true;
                    _log.LogInformation(
                        "Desk ready: {Stored} monitor(s) on file, {Reported} readable here, {Shown} on the desk",
                        _config.Monitors.Count, inventory.Monitors?.Count ?? 0, _snapshot.Monitors.Count);
                }
            }
            else
            {
                await SendToControllerAsync(MessageSerializer.Encode(MessageKind.DeskInventory, inventory), cancel);
            }
        }
        catch (OperationCanceledException) when (round.IsCancellationRequested && !cancelToken.IsCancellationRequested)
        {
            if (_roundFailures++ % 10 == 0)
                _log.LogError("The desk gave up on a round after 30 seconds. Something it depends on is not "
                    + "answering; it will keep the monitors it already had and try again");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_roundFailures++ % 10 == 0)
                _log.LogError(ex, "The desk could not finish a round -- it will keep the monitors it already had");
            throw;
        }
    }

    private bool _roundCompleted;
    private int _roundFailures;

    // The peer list cannot come from the PeersChanged event alone. This service is constructed after
    // the relay connection, so on a desk whose peers connect early the event has already fired by
    // the time anything here is listening — and then it never fires again, because nothing changes.
    // The controller would sit with an empty peer list forever: no desk state broadcast, no config
    // pushed, and every other computer stuck on "waiting for the computers to report their screens".
    private async Task RefreshPeersAsync()
    {
        var known = new List<string>(_peers);
        void Note(string? host)
        {
            if (!string.IsNullOrWhiteSpace(host)
                && !host.Equals(LocalName, StringComparison.OrdinalIgnoreCase)
                && !known.Contains(host, StringComparer.OrdinalIgnoreCase))
                known.Add(host);
        }

        // Anyone who has sent us a desk message is demonstrably reachable, whatever the event said.
        foreach (var host in _reports.Keys) Note(host);
        foreach (var host in _replied) Note(host);
        try
        {
            if (IsController) foreach (var host in (await _world.GetPeerScreensSnapshot()).Keys) Note(host);
            else foreach (var host in await _world.GetMasters()) Note(host);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Could not read the relay's peer list"); }

        var added = known.Except(_peers, StringComparer.OrdinalIgnoreCase).ToArray();
        if (added.Length == 0) return;
        _peers = [.. known];
        Greet(added);
    }

    // A computer that has just become visible needs the desk document before its settings window can
    // show anything, and the current desk state so it does not sit on a placeholder for four seconds.
    private void Greet(string[] added)
    {
        if (!IsController || added.Length == 0) return;
        try
        {
            Send(added, MessageSerializer.Encode(MessageKind.DeskConfigPush, new DeskConfigPushMessage(_store.Serialize(_config))));
            Broadcast();
        }
        catch (Exception ex) { _log.LogDebug(ex, "Could not send the desk to {Peers}", string.Join(", ", added)); }
    }

    // -- inventory -------------------------------------------------------------------------------

    private async Task<DeskInventoryMessage> BuildInventoryAsync(CancellationToken cancel)
    {
        var monitors = new List<DeskMonitorReport>();
        try
        {
            foreach (var monitor in await _router.InventoryAsync(cancel))
                monitors.Add(new DeskMonitorReport(
                    monitor.Id, monitor.Description, monitor.CurrentInput,
                    monitor.Aliases?.ToList(), monitor.SupportedInputs?.ToList()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Monitor inventory unavailable");
        }

        var screens = new List<DeskScreenReport>();
        try
        {
            var snapshot = await _screens.Get(cancel);
            foreach (var screen in snapshot.Screens)
                screens.Add(new DeskScreenReport(
                    screen.Identity?.ScreenName ?? screen.Name,
                    screen.Identity?.Output,
                    screen.Identity?.DisplayName,
                    screen.X, screen.Y, screen.Width, screen.Height,
                    screen.Identity?.PlatformId));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Screen inventory unavailable");
        }

        return new DeskInventoryMessage(monitors, screens);
    }

    // -- merge and broadcast ---------------------------------------------------------------------

    private async Task RecomputeAsync(CancellationToken cancel, bool forcePush = false)
    {
        _log.LogDebug("Desk recompute: waiting for the desk lock");
        await _gate.WaitAsync(cancel);
        _log.LogDebug("Desk recompute: merging");
        try
        {
            var merge = DeskMerge.Merge(_config.Monitors, _reports, _optimistic, KnownHosts());
            if (merge.ConfigChanged)
            {
                _config = WithMonitors(_config, merge.Monitors);
                await PersistAsync(push: true);
            }
            else if (forcePush) await PersistAsync(push: true);

            // Once a monitor reports the computer we asked for, the optimistic guess has served
            // its purpose and the real reports take over.
            foreach (var (id, host) in _optimistic.ToList())
                if (merge.Views.FirstOrDefault(v => v.Id == id) is { } view && view.Sources.Any(s => s.Reachable && s.Host.Equals(host, StringComparison.OrdinalIgnoreCase)))
                    _optimistic.Remove(id);

            _log.LogDebug("Desk recompute: merged into {Count} monitor(s)", merge.Views.Count);
            _snapshot = BuildSnapshot(merge.Views);

            // The arrangement is the source of truth for where the pointer crosses. Deriving the
            // crossings only when someone presses Save leaves a desk whose monitors have moved —
            // or whose stored edges came from an older, worse rule — quietly unable to cross at
            // all, which is indistinguishable from the feature being broken.
            // Written, never self-restarted. Restarting here to apply the new crossings turned a
            // single disagreement into an unbreakable loop: the config is parsed with mirrors
            // already expanded and rebuilt without them, so the comparison could never match, and
            // the controller rewrote and restarted every few seconds — long before the relay had
            // time to connect, so no peer ever joined and the desk stayed empty. The comparison is
            // normalised now, but the restart stays gone: the crossings take effect at the next
            // start, and no future disagreement can cost more than a stale edge until then.
            if (IsController && _snapshot.Monitors.Count > 0)
            {
                var rebuilt = RebuildHosts(_config);
                if (!SameTopology(_config, rebuilt))
                {
                    _log.LogInformation("Desk crossings changed; applying the new computer layout");
                    _config = rebuilt;
                    await PersistAsync(push: true);
                    ApplyLayout();
                }
            }

            _log.LogDebug("Desk recompute: broadcasting");
            Broadcast();
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    // One spelling per computer. The relay, the config and this machine's own name do not always
    // agree on capitalisation, and every place that compares them case-sensitively invents a peer.
    private List<string> KnownHosts()
    {
        var hosts = new List<string> { LocalName };
        foreach (var name in _profile.Hosts.Select(h => h.Name)
                     .Concat(_peers)
                     .Concat(_config.Monitors.SelectMany(m => m.Sources.Select(s => s.Host)))
                     .Concat(_reports.Keys))
            if (!string.IsNullOrWhiteSpace(name) && !hosts.Contains(name, StringComparer.OrdinalIgnoreCase))
                hosts.Add(name);
        return hosts;
    }

    private DeskSnapshot BuildSnapshot(IReadOnlyList<DeskMonitorView> views)
    {
        var hosts = KnownHosts();
        foreach (var name in views.SelectMany(v => v.Sources.Select(s => s.Host)))
            if (!hosts.Contains(name, StringComparer.OrdinalIgnoreCase)) hosts.Add(name);

        return new DeskSnapshot(
            Controller: string.IsNullOrWhiteSpace(_controller) ? LocalName : _controller,
            LocalHost: LocalName,
            Hosts: hosts,
            ConnectedHosts: _peers,
            Monitors: views,
            // Read from the document rather than the scene coordinator, whose list was fixed at
            // startup: a setup saved a moment ago has to appear straight away, not after a restart.
            Scenes: _config.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.ProfileName)).Select(p => p.ProfileName!).ToList(),
            CurrentScene: _scenes.CurrentScene,
            IsController: IsController,
            Crossings: Crossings());
    }

    // Where the pointer can move between computers, mirror-expanded so both halves of each crossing
    // are listed. An empty list here means the desk cannot be left, however right it looks.
    private List<string> Crossings()
    {
        var profile = _config.Profiles.FirstOrDefault(p =>
            string.Equals(p.ProfileName, _scenes.CurrentScene, StringComparison.OrdinalIgnoreCase))
            ?? _config.Profiles.FirstOrDefault();
        if (profile == null) return [];

        var hosts = profile.Hosts.Select(h => new HostConfig
        {
            Name = h.Name,
            Neighbours = h.Neighbours.Select(n => new NeighbourConfig
            {
                Direction = n.Direction, Name = n.Name, Mirror = n.Mirror,
                SourceScreen = n.SourceScreen, DestScreen = n.DestScreen,
                SourceStart = n.SourceStart, SourceEnd = n.SourceEnd,
                DestStart = n.DestStart, DestEnd = n.DestEnd,
            }).ToList(),
        }).ToList();
        HydraConfig.ExpandMirrors(hosts);

        return hosts
            .SelectMany(h => h.Neighbours.Select(n => $"{h.Name} {n.Direction} {n.Name}"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void Broadcast()
    {
        if (!IsController || _peers.Length == 0) return;
        var message = new DeskStateMessage(
            DeskConfigStore.Fingerprint(_config),
            _snapshot.Controller,
            _snapshot.Hosts.ToList(),
            _snapshot.ConnectedHosts.ToList(),
            _snapshot.Monitors.Select(m => new DeskStateMonitor(
                m.Id, m.Label, m.DeskX, m.DeskY, m.Width, m.Height, m.ActiveHost,
                m.Sources.Select(s => new DeskStateSource(s.Host, s.Input, s.Reachable)).ToList())).ToList(),
            _snapshot.Scenes.ToList(),
            _snapshot.CurrentScene);
        _log.LogDebug("Desk state -> {Peers}: {Monitors} monitor(s)", string.Join(",", _peers), message.Monitors.Count);
        Send(_peers, MessageSerializer.Encode(MessageKind.DeskState, message));
    }

    // -- public actions --------------------------------------------------------------------------

    public Task<DeskActionResult> SetMonitorHostAsync(string monitorId, string host, CancellationToken cancellationToken = default) =>
        IsController
            ? SetMonitorHostCoreAsync(monitorId, host, cancellationToken)
            : Task.FromResult(Forward(new DeskCommandMessage(DeskCommandKind.SetMonitorHost, Monitor: monitorId, Host: host)));

    public async Task<DeskActionResult> SetControllerAsync(string host, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return DeskActionResult.Fail("Pick a computer to hand control to.");
        if (host.Equals(_snapshot.Controller, StringComparison.OrdinalIgnoreCase))
            return DeskActionResult.Ok($"{host} already has the keyboard and mouse.");
        if (!_snapshot.Hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            return DeskActionResult.Fail($"'{host}' is not part of this desk.");
        if (!host.Equals(LocalName, StringComparison.OrdinalIgnoreCase) && !_peers.Contains(host, StringComparer.OrdinalIgnoreCase))
            return DeskActionResult.Fail($"{host} is not connected, so it cannot take control yet.");

        // Handing over is symmetric: whoever asks tells everyone, including the computer that has
        // control today, and every agent restarts into its new role.
        Send(_peers, MessageSerializer.Encode(MessageKind.DeskCommand, new DeskCommandMessage(DeskCommandKind.SetController, Host: host)));
        ApplyController(host);
        await Task.CompletedTask;
        return DeskActionResult.Ok($"Handing the keyboard and mouse to {host}…");
    }

    public Task<DeskActionResult> SaveSceneAsync(string name, CancellationToken cancellationToken = default) =>
        IsController
            ? SaveSceneCoreAsync(name)
            : Task.FromResult(Forward(new DeskCommandMessage(DeskCommandKind.SaveScene, Scene: name)));

    public Task<DeskActionResult> DeleteSceneAsync(string name, CancellationToken cancellationToken = default) =>
        IsController
            ? DeleteSceneCoreAsync(name)
            : Task.FromResult(Forward(new DeskCommandMessage(DeskCommandKind.DeleteScene, Scene: name)));

    public async Task<DeskActionResult> ActivateSceneAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!IsController) return Forward(new DeskCommandMessage(DeskCommandKind.ActivateScene, Scene: name));
        // A scene carries its own controller, so an override taken by hand must not outlive it.
        _controllerStore.Clear();
        var result = await _scenes.ActivateAsync(name, cancellationToken);
        return new DeskActionResult(result.Accepted, result.Message);
    }

    public Task<DeskActionResult> ProbeInputAsync(string monitorId, string host, int input, CancellationToken cancellationToken = default) =>
        IsController
            ? ProbeInputCoreAsync(monitorId, host, input, cancellationToken)
            : Task.FromResult(Forward(new DeskCommandMessage(DeskCommandKind.ProbeInput, Monitor: monitorId, Host: host, Input: input)));

    public Task<DeskActionResult> SaveArrangementAsync(IReadOnlyList<DeskPlacement> placements, CancellationToken cancellationToken = default) =>
        IsController
            ? SaveArrangementCoreAsync(placements)
            : Task.FromResult(Forward(new DeskCommandMessage(DeskCommandKind.SaveArrangement,
                Arrangement: placements.Select(p => new DeskArrangementEntry(p.Monitor, p.DeskX, p.DeskY, p.Width, p.Height, p.Label)).ToList())));

    // -- controller-side implementations ---------------------------------------------------------

    private async Task<DeskActionResult> SetMonitorHostCoreAsync(string monitorId, string host, CancellationToken cancel)
    {
        var monitor = _config.Monitor(monitorId);
        if (monitor == null) return DeskActionResult.Fail($"Unknown monitor '{monitorId}'.");
        var view = _snapshot.Monitors.FirstOrDefault(m => m.Id == monitorId);

        if (view?.ActiveHost?.Equals(host, StringComparison.OrdinalIgnoreCase) == true)
            return DeskActionResult.Ok($"{monitor.DisplayName()} already shows {host}.");

        // The computer that actually reported the monitor this round is the one that can command it,
        // not the one an earlier switch optimistically put there.
        var owner = view?.Sources.FirstOrDefault(s => s.Reachable)?.Host ?? view?.ActiveHost;
        var ddcId = owner == null ? null : monitor.Source(owner)?.DdcId;
        var target = monitor.Source(host);

        DeskActionResult result;
        if (target?.Input != null && !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(ddcId))
        {
            // Wake the computer we are switching *to* first, and give it a moment.
            //
            // A monitor asked for an input nothing is driving finds no signal and goes hunting for
            // one, which lands it straight back on the computer it just left -- a switch that goes
            // black and undoes itself a few seconds later. The same switch works perfectly when that
            // computer happens to be awake, which is exactly what makes it look intermittent rather
            // than like the missing step it is.
            await WakeForSwitchAsync(host, cancel);

            result = await SwitchAsync(owner!, ddcId!, target.Input.Value, cancel);
            if (!result.Accepted) result = ExplainSwitchGap(monitor, owner, host, result.Message);
        }
        else result = ExplainSwitchGap(monitor, owner, host, null);
        if (!result.Accepted) return result;

        _optimistic[monitorId] = host;
        _snapshot = BuildSnapshot(_snapshot.Monitors
            .Select(m => m.Id == monitorId ? m with { ActiveHost = host } : m).ToList());

        // The monitor now belongs to someone else, so the pointer crosses somewhere else. Derived
        // and handed over now rather than at the next round: waiting meant the crossings stayed
        // where the monitor used to be for several seconds, and the only way through was to keep
        // moving the mouse until they caught up.
        var rebuilt = RebuildHosts(_config);
        if (!SameTopology(_config, rebuilt))
        {
            _config = rebuilt;
            ApplyLayout();
            await PersistAsync(push: true);
        }

        Broadcast();
        Changed?.Invoke();
        return DeskActionResult.Ok($"{monitor.DisplayName()} switched to {host}.");
    }

    // Asks a computer to drive its displays again, so that a monitor switched to it finds a signal.
    //
    // Best effort on purpose: a computer that cannot be reached, or is too old to understand the
    // request, must not stop the switch. It only ever made things worse to refuse.
    private async Task WakeForSwitchAsync(string host, CancellationToken cancel)
    {
        try
        {
            if (host.Equals(LocalName, StringComparison.OrdinalIgnoreCase))
            {
                await _router.SetDisplayPowerAsync(wake: true, cancel);
            }
            else
            {
                var requestId = Guid.NewGuid().ToString("N");
                var completion = new TaskCompletionSource<DeskSetInputResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[requestId] = completion;
                try
                {
                    Send([host], MessageSerializer.Encode(MessageKind.DeskDisplayPower, new DeskDisplayPowerMessage(requestId, Wake: true)));
                    await completion.Task.WaitAsync(RemoteTimeout, cancel);
                }
                finally { _pending.TryRemove(requestId, out _); }
            }

            // Output does not come back the instant it is asked for. Switching into a signal that is
            // still arriving is the same as switching into no signal at all.
            var settle = _config.Profiles.FirstOrDefault()?.DisplayRouting.SettleDelayMs ?? 500;
            if (settle > 0) await Task.Delay(settle, cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Could not wake {Host} before switching a monitor to it", host);
        }
    }

    // Explains what is missing and what to do about it.
    //
    // There is deliberately no automatic fallback here. Putting the losing computer's displays to
    // sleep looks like it would work and does not: ScreenFuse is forwarding input to that computer,
    // so the first mouse move wakes the display straight back up and the monitor returns. Video
    // output would have to be switched off rather than idled, and no operating system offers that
    // for one monitor of several. Saying what is missing beats doing something that undoes itself.
    private DeskActionResult ExplainSwitchGap(DeskMonitorConfig monitor, string? owner, string host, string? ddcFailure)
    {
        if (host.Equals(owner, StringComparison.OrdinalIgnoreCase))
            return DeskActionResult.Ok($"{monitor.DisplayName()} already shows {host}.");
        if (ddcFailure != null)
            return DeskActionResult.Fail($"{monitor.DisplayName()} refused the input switch: {ddcFailure}");
        if (string.IsNullOrWhiteSpace(owner))
            return DeskActionResult.Fail($"No connected computer can reach {monitor.DisplayName()} right now, so its input cannot be switched.");

        // The monitor has already said which codes it accepts, so name the ones still unaccounted
        // for — that is a short list to try rather than an open question.
        var taken = monitor.Sources.Where(s => s.Input != null).Select(s => s.Input!.Value).ToHashSet();
        var candidates = (monitor.Source(owner!)?.AvailableInputs ?? []).Where(i => !taken.Contains(i)).ToList();
        var hint = candidates.Count > 0
            ? $" It accepts {string.Join(", ", candidates)} besides the codes already known — try those under 'How each computer is wired'."
            : " Set it under 'How each computer is wired'.";
        return DeskActionResult.Fail($"ScreenFuse does not know which input on {monitor.DisplayName()} shows {host}.{hint}");
    }

    private async Task<DeskActionResult> SwitchAsync(string owner, string ddcId, int input, CancellationToken cancel)
    {
        if (owner.Equals(LocalName, StringComparison.OrdinalIgnoreCase))
        {
            var local = await _router.SetInputAsync(ddcId, input, cancel);
            return new DeskActionResult(local.Success, local.Success ? "Switched." : $"Switch failed: {local.Detail}");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<DeskSetInputResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;
        try
        {
            Send([owner], MessageSerializer.Encode(MessageKind.DeskSetInput, new DeskSetInputMessage(requestId, ddcId, input)));
            var reply = await completion.Task.WaitAsync(RemoteTimeout, cancel);
            return new DeskActionResult(reply.Success, reply.Success ? "Switched." : $"{owner} could not switch the monitor: {reply.Detail}");
        }
        catch (TimeoutException)
        {
            return DeskActionResult.Fail($"{owner} did not answer the switch request.");
        }
        finally { _pending.TryRemove(requestId, out _); }
    }

    private async Task<DeskActionResult> ProbeInputCoreAsync(string monitorId, string host, int input, CancellationToken cancel)
    {
        var monitor = _config.Monitor(monitorId);
        if (monitor == null) return DeskActionResult.Fail($"Unknown monitor '{monitorId}'.");
        if (input is < 0 or > 255) return DeskActionResult.Fail("An input code must be between 0 and 255.");

        await _gate.WaitAsync(cancel);
        try
        {
            _config = WithMonitors(_config, _config.Monitors
                .Select(m => m.Id != monitorId ? m : m.With(sources: Upsert(m.Sources, host, input)))
                .ToList());
            await PersistAsync(push: true);
        }
        finally { _gate.Release(); }

        var view = _snapshot.Monitors.FirstOrDefault(m => m.Id == monitorId);
        var owner = view?.Sources.FirstOrDefault(s => s.Reachable)?.Host;
        if (string.IsNullOrWhiteSpace(owner))
            return DeskActionResult.Ok($"Saved input {input} for {host}. No computer can reach {monitor.DisplayName()} right now, so it was not tried.");
        var ddcId = _config.Monitor(monitorId)?.Source(owner!)?.DdcId;
        if (string.IsNullOrWhiteSpace(ddcId))
            return DeskActionResult.Ok($"Saved input {input} for {host}.");

        var result = await SwitchAsync(owner!, ddcId!, input, cancel);
        return result.Accepted
            ? DeskActionResult.Ok($"Saved input {input} for {host} and switched {monitor.DisplayName()} to it — check whether {host} is on screen.")
            : DeskActionResult.Ok($"Saved input {input} for {host}, but the monitor did not accept it: {result.Message}");
    }

    private static List<MonitorSourceConfig> Upsert(List<MonitorSourceConfig> sources, string host, int input)
    {
        var result = sources.Where(s => !s.Host.Equals(host, StringComparison.OrdinalIgnoreCase)).ToList();
        var existing = sources.FirstOrDefault(s => s.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
        result.Add(new MonitorSourceConfig
        {
            Host = host,
            Input = input,
            DdcId = existing?.DdcId,
            ScreenId = existing?.ScreenId,
            AvailableInputs = existing?.AvailableInputs ?? [],
        });
        return result;
    }

    private async Task<DeskActionResult> SaveArrangementCoreAsync(IReadOnlyList<DeskPlacement> placements)
    {
        await _gate.WaitAsync();
        try
        {
            var byId = placements.ToDictionary(p => p.Monitor, StringComparer.OrdinalIgnoreCase);
            var monitors = _config.Monitors.Select(m => !byId.TryGetValue(m.Id, out var p) ? m : m.With(
                label: string.IsNullOrWhiteSpace(p.Label) ? m.Label : p.Label,
                deskX: p.DeskX, deskY: p.DeskY,
                width: p.Width > 0 ? p.Width : m.Width,
                height: p.Height > 0 ? p.Height : m.Height)).ToList();

            _config = WithMonitors(_config, monitors);
            _config = RebuildHosts(_config);
            // Handed to the router here, not merely written. Rebuilding and persisting without this
            // left the new crossings sitting in the file while the pointer went on using the old
            // ones, so moving a monitor did nothing until the agent was restarted — and the desk on
            // screen disagreed with the desk you could feel. Recompute cannot do it either: it
            // applies only when the derived layout differs from the stored one, and by this point
            // they are the same.
            ApplyLayout();
            await PersistAsync(push: true);
        }
        finally { _gate.Release(); }

        await RecomputeAsync(CancellationToken.None);
        return DeskActionResult.Ok("Arrangement saved. The crossing edges were rebuilt from it.");
    }

    private async Task<DeskActionResult> SaveSceneCoreAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return DeskActionResult.Fail("A profile needs a name.");

        await _gate.WaitAsync();
        try
        {
            var assignments = _snapshot.Monitors
                .Where(m => !string.IsNullOrWhiteSpace(m.ActiveHost))
                .Select(m => new MonitorAssignmentConfig { Monitor = m.Id, Host = m.ActiveHost! })
                .ToList();

            var template = _config.Profiles.FirstOrDefault(p =>
                string.Equals(p.ProfileName, _profile.ProfileName, StringComparison.OrdinalIgnoreCase))
                ?? _config.Profiles.FirstOrDefault();
            if (template == null) return DeskActionResult.Fail("There is no profile to copy the connection settings from.");

            var saved = new HydraConfig
            {
                ProfileName = name,
                Controller = _snapshot.Controller,
                Mode = template.Mode,
                Hosts = [],
                DisplayRouting = new DisplayRoutingConfig
                {
                    Monitors = assignments,
                    Inputs = [],
                    WakeDisplays = template.DisplayRouting.WakeDisplays,
                    SleepDisplays = false,
                    SettleDelayMs = template.DisplayRouting.SettleDelayMs,
                },
                NetworkConfig = template.NetworkConfig,
                EmbeddedStyx = template.EmbeddedStyx,
                EmbeddedStyxServer = template.EmbeddedStyxServer,
                MouseScale = template.MouseScale,
                RelativeMouseScale = template.RelativeMouseScale,
                ScreenDefinitions = template.ScreenDefinitions,
                HideCursor = template.HideCursor,
                RemoteOnly = template.RemoteOnly,
                SyncScreensaver = template.SyncScreensaver,
                ScreenLockPropagation = template.ScreenLockPropagation,
                AccelerateMouseWheel = template.AccelerateMouseWheel,
                UnicodeKeyRepeat = template.UnicodeKeyRepeat,
                DeadCorners = template.DeadCorners,
            };

            var profiles = _config.Profiles
                .Where(p => !string.Equals(p.ProfileName, name, StringComparison.OrdinalIgnoreCase))
                .Append(saved)
                .ToList();
            _config = WithProfiles(_config, profiles);
            _config = RebuildHosts(_config);
            await PersistAsync(push: true);
        }
        finally { _gate.Release(); }

        Changed?.Invoke();
        return DeskActionResult.Ok($"Saved '{name}'. Restart or pick it from the tray to switch back to it later.");
    }

    private async Task<DeskActionResult> DeleteSceneCoreAsync(string name)
    {
        if (string.Equals(name, _profile.ProfileName, StringComparison.OrdinalIgnoreCase))
            return DeskActionResult.Fail("That profile is active right now. Switch to another one first.");
        await _gate.WaitAsync();
        try
        {
            var profiles = _config.Profiles.Where(p => !string.Equals(p.ProfileName, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (profiles.Count == _config.Profiles.Count) return DeskActionResult.Fail($"No profile named '{name}'.");
            if (profiles.Count == 0) return DeskActionResult.Fail("The desk needs at least one profile.");
            _config = WithProfiles(_config, profiles);
            await PersistAsync(push: true);
        }
        finally { _gate.Release(); }

        Changed?.Invoke();
        return DeskActionResult.Ok($"Deleted '{name}'.");
    }

    // Every scene keeps its own crossing edges, because they depend on which computer each monitor
    // shows in that scene — the same physical arrangement produces a different graph per assignment.
    private HydraConfigFile RebuildHosts(HydraConfigFile file)
    {
        var allHosts = _snapshot.Hosts;
        var profiles = file.Profiles.Select(profile =>
        {
            var placed = DeskArrangement.Place(file.Monitors, id =>
                profile.DisplayRouting.HostFor(id)
                ?? _snapshot.Monitors.FirstOrDefault(m => m.Id == id)?.ActiveHost
                // A computer that is momentarily away still owns its monitors. Without this, a peer
                // restarting erases every crossing to it — and that emptied layout is then written
                // and pushed, so the desk destroys itself the moment the other machine blinks.
                ?? OnlyOwnerOf(file, id));
            return Clone(profile, DeskArrangement.BuildHosts(placed, allHosts));
        }).ToList();
        return WithProfiles(file, profiles);
    }

    // Hands the current arrangement to the input router, mirror-expanded so both ways round exist.
    private void ApplyLayout()
    {
        var profile = _config.Profiles.FirstOrDefault(p =>
            string.Equals(p.ProfileName, _scenes.CurrentScene, StringComparison.OrdinalIgnoreCase))
            ?? _config.Profiles.FirstOrDefault();
        if (profile == null) return;

        var hosts = profile.Hosts.Select(h => new HostConfig
        {
            Name = h.Name,
            DeadCorners = h.DeadCorners,
            Neighbours = h.Neighbours.Select(n => new NeighbourConfig
            {
                Direction = n.Direction, Name = n.Name, Mirror = n.Mirror,
                SourceScreen = n.SourceScreen, DestScreen = n.DestScreen,
                SourceStart = n.SourceStart, SourceEnd = n.SourceEnd,
                DestStart = n.DestStart, DestEnd = n.DestEnd,
            }).ToList(),
        }).ToList();
        HydraConfig.ExpandMirrors(hosts);
        _profile.ApplyHosts(hosts);
    }

    // The computer a monitor belongs to when only one is wired to it. Not a guess: a monitor with a
    // single source has nowhere else it could be showing.
    private static string? OnlyOwnerOf(HydraConfigFile file, string monitorId)
    {
        var monitor = file.Monitors.FirstOrDefault(m => m.Id == monitorId);
        return monitor?.Sources.Count == 1 ? monitor.Sources[0].Host : null;
    }

    // Compares only what the pointer cares about: who is next to whom, in which direction.
    //
    // Both sides are mirror-expanded first. A config read from disk has already been through
    // ExpandMirrors, while a freshly derived one has not, so comparing them as written makes an
    // identical layout look like a change — every single round.
    private static bool SameTopology(HydraConfigFile a, HydraConfigFile b)
    {
        static List<string> Edges(HydraConfigFile file) => file.Profiles
            .SelectMany(p =>
            {
                var hosts = p.Hosts.Select(h => new HostConfig
                {
                    Name = h.Name,
                    DeadCorners = h.DeadCorners,
                    Neighbours = h.Neighbours.Select(n => new NeighbourConfig
                    {
                        Direction = n.Direction, Name = n.Name, Mirror = n.Mirror,
                        SourceScreen = n.SourceScreen, DestScreen = n.DestScreen,
                        SourceStart = n.SourceStart, SourceEnd = n.SourceEnd,
                        DestStart = n.DestStart, DestEnd = n.DestEnd,
                    }).ToList(),
                }).ToList();
                HydraConfig.ExpandMirrors(hosts);
                return hosts.SelectMany(h => h.Neighbours.Select(n =>
                    $"{p.ProfileName}|{h.Name}|{n.Direction}|{n.Name}|{n.SourceScreen}|{n.DestScreen}".ToLowerInvariant()));
            })
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        return Edges(a).SequenceEqual(Edges(b), StringComparer.Ordinal);
    }

    private static HydraConfig Clone(HydraConfig source, List<HostConfig> hosts) => new()
    {
        ProfileName = source.ProfileName,
        Controller = source.Controller,
        Mode = source.Mode,
        Hosts = hosts,
        DisplayRouting = source.DisplayRouting,
        Conditions = source.Conditions,
        NetworkConfig = source.NetworkConfig,
        EmbeddedStyx = source.EmbeddedStyx,
        EmbeddedStyxServer = source.EmbeddedStyxServer,
        MouseScale = source.MouseScale,
        RelativeMouseScale = source.RelativeMouseScale,
        ScreenDefinitions = source.ScreenDefinitions,
        HideCursor = source.HideCursor,
        RemoteOnly = source.RemoteOnly,
        SyncScreensaver = source.SyncScreensaver,
        ScreenLockPropagation = source.ScreenLockPropagation,
        AccelerateMouseWheel = source.AccelerateMouseWheel,
        UnicodeKeyRepeat = source.UnicodeKeyRepeat,
        DeadCorners = source.DeadCorners,
    };

    private static HydraConfigFile WithMonitors(HydraConfigFile file, List<DeskMonitorConfig> monitors) =>
        Rebuild(file, monitors, file.Profiles);

    private static HydraConfigFile WithProfiles(HydraConfigFile file, List<HydraConfig> profiles) =>
        Rebuild(file, file.Monitors, profiles);

    private static HydraConfigFile Rebuild(HydraConfigFile file, List<DeskMonitorConfig> monitors, List<HydraConfig> profiles) => new()
    {
        Name = file.Name,
        AutoUpdate = file.AutoUpdate,
        LogLevel = file.LogLevel,
        LockFile = file.LockFile,
        LogFile = file.LogFile,
        SessionLogFile = file.SessionLogFile,
        LogTruncate = file.LogTruncate,
        Profile = file.Profile,
        ControlPort = file.ControlPort,
        DebugShield = file.DebugShield,
        DebugMouse = file.DebugMouse,
        Monitors = monitors,
        Profiles = profiles,
    };

    private async Task PersistAsync(bool push)
    {
        if (_configUnreadable)
        {
            _log.LogWarning("Not writing the desk document: the one on disk could not be read, and "
                + "replacing it with what this computer has would destroy it");
            return;
        }
        try { await _store.SaveAsync(_config); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not write the desk document");
            return;
        }
        if (push && _peers.Length > 0)
        {
            try
            {
                var json = _store.Serialize(_config);
                Send(_peers, MessageSerializer.Encode(MessageKind.DeskConfigPush, new DeskConfigPushMessage(json)));
            }
            catch (Exception ex) { _log.LogWarning(ex, "Could not share the desk document with the peers"); }
        }
    }

    // -- relay -----------------------------------------------------------------------------------

    private Task OnPeersChanged(string[] peers)
    {
        var added = peers.Except(_peers, StringComparer.OrdinalIgnoreCase).ToArray();
        _peers = peers;
        foreach (var gone in _reports.Keys.Where(k => !k.Equals(LocalName, StringComparison.OrdinalIgnoreCase) && !peers.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            _reports.TryRemove(gone, out _);
        _replied.RemoveWhere(h => !peers.Contains(h, StringComparer.OrdinalIgnoreCase));
        Greet(added);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    private async Task OnMessageReceived(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        if (kind is MessageKind.DeskInventory or MessageKind.DeskState or MessageKind.DeskCommand
            or MessageKind.DeskConfigPush or MessageKind.DeskSetInput or MessageKind.DeskSetInputResult
            or MessageKind.DeskDisplayPower)
        {
            _replied.Add(sourceHost);
            _log.LogDebug("Desk message {Kind} from {Host} (controller={IsController})", kind, sourceHost, IsController);
        }

        switch (kind)
        {
            case MessageKind.DeskInventory when IsController:
                _reports[sourceHost] = new DecodedMessage(kind, body).Deserialize<DeskInventoryMessage>();
                await RecomputeAsync(CancellationToken.None);
                break;

            case MessageKind.DeskSetInput:
            {
                var request = new DecodedMessage(kind, body).Deserialize<DeskSetInputMessage>();
                var result = await _router.SetInputAsync(request.DdcId, request.Input);
                Send([sourceHost], MessageSerializer.Encode(MessageKind.DeskSetInputResult,
                    new DeskSetInputResultMessage(request.RequestId, result.Success, result.Detail)));
                break;
            }

            case MessageKind.DeskConfigRequest when IsController:
                Greet([sourceHost]);
                break;

            case MessageKind.DeskDisplayPower:
            {
                var request = new DecodedMessage(kind, body).Deserialize<DeskDisplayPowerMessage>();
                var result = await _router.SetDisplayPowerAsync(request.Wake);
                Send([sourceHost], MessageSerializer.Encode(MessageKind.DeskSetInputResult,
                    new DeskSetInputResultMessage(request.RequestId, result.Success, result.Detail)));
                break;
            }

            case MessageKind.DeskSetInputResult:
            {
                var reply = new DecodedMessage(kind, body).Deserialize<DeskSetInputResultMessage>();
                if (_pending.TryGetValue(reply.RequestId, out var completion)) completion.TrySetResult(reply);
                break;
            }

            case MessageKind.DeskState when !IsController:
            {
                var state = new DecodedMessage(kind, body).Deserialize<DeskStateMessage>();
                _controller = state.Controller;
                _snapshot = new DeskSnapshot(
                    state.Controller, LocalName, state.Hosts, state.ConnectedHosts,
                    state.Monitors.Select(m => new DeskMonitorView(
                        m.Id, m.Label, m.DeskX, m.DeskY, m.Width, m.Height, m.ActiveHost,
                        m.Sources.Select(s => new DeskSourceView(s.Host, s.Input, s.Reachable)).ToList())).ToList(),
                    state.Scenes, state.CurrentScene, IsController: false, Crossings: Crossings());

                // The desk we are shown and the desk we hold are different things: the picture
                // arrives every few seconds, the document only when the controller decides to send
                // it. A computer that restarted, or that missed the one push it was ever sent, would
                // otherwise keep a stale desk for good — which is exactly how a follower ended up
                // with every monitor and no crossings, unable to send the pointer back.
                if (state.Fingerprint != null && state.Fingerprint != DeskConfigStore.Fingerprint(_config))
                {
                    _log.LogDebug("Desk document differs from {Host}; asking for it", sourceHost);
                    Send([sourceHost], MessageSerializer.Encode(MessageKind.DeskConfigRequest, new DeskConfigRequestMessage()));
                }

                Changed?.Invoke();
                break;
            }

            case MessageKind.DeskCommand:
                await HandleCommandAsync(sourceHost, new DecodedMessage(kind, body).Deserialize<DeskCommandMessage>());
                break;

            case MessageKind.DeskConfigPush:
                await ApplyPushedConfigAsync(sourceHost, new DecodedMessage(kind, body).Deserialize<DeskConfigPushMessage>());
                break;
        }
    }

    private async Task HandleCommandAsync(string sourceHost, DeskCommandMessage command)
    {
        // Control handover is the one command every computer acts on, not just the controller —
        // it is precisely the message that tells the current controller to step down.
        if (command.Kind == DeskCommandKind.SetController)
        {
            if (!string.IsNullOrWhiteSpace(command.Host)) ApplyController(command.Host!);
            return;
        }

        if (!IsController) return;
        _log.LogInformation("Desk command {Kind} from {Host}", command.Kind, sourceHost);
        var result = command.Kind switch
        {
            DeskCommandKind.SetMonitorHost => await SetMonitorHostCoreAsync(command.Monitor ?? "", command.Host ?? "", CancellationToken.None),
            DeskCommandKind.SaveScene => await SaveSceneCoreAsync(command.Scene ?? ""),
            DeskCommandKind.DeleteScene => await DeleteSceneCoreAsync(command.Scene ?? ""),
            DeskCommandKind.ActivateScene => await ActivateSceneAsync(command.Scene ?? ""),
            DeskCommandKind.ProbeInput => await ProbeInputCoreAsync(command.Monitor ?? "", command.Host ?? "", command.Input ?? 0, CancellationToken.None),
            DeskCommandKind.SaveArrangement => await SaveArrangementCoreAsync(
                (command.Arrangement ?? []).Select(a => new DeskPlacement(a.Monitor, a.DeskX, a.DeskY, a.Width, a.Height, a.Label)).ToList()),
            _ => DeskActionResult.Fail($"Unsupported desk command {command.Kind}."),
        };
        if (!result.Accepted) _log.LogWarning("Desk command {Kind} from {Host} refused: {Message}", command.Kind, sourceHost, result.Message);
    }

    private void ApplyController(string host)
    {
        try { _controllerStore.Write(host); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not record the new controller");
            return;
        }
        _controller = host;
        _log.LogInformation("Control moves to {Host}; restarting into the new role", host);
        ScheduleRestart();
    }

    private async Task ApplyPushedConfigAsync(string sourceHost, DeskConfigPushMessage push)
    {
        if (IsController) return;
        try
        {
            var incoming = DeskConfigStore.Parse(push.Json, _store.Path);
            var local = _store.Load();
            var merged = DeskConfigStore.Merge(local, incoming);
            // Compared on everything the desk shares, crossings included. An earlier check looked at
            // the monitors alone, so a document that differed only in where the pointer may cross
            // was judged identical and never written — leaving a follower with every monitor, no
            // crossings, and no way to notice.
            if (DeskConfigStore.Fingerprint(local) == DeskConfigStore.Fingerprint(merged)) return;
            // A restart would read this file back, so it has to be on disk first either way.
            await _store.SaveAsync(merged);
            _config = merged;

            // A rearranged desk is adopted where it stands. Restarting to pick up new crossings
            // dropped the relay for several seconds every time the arrangement was nudged, which is
            // no way to treat someone dragging a monitor around a settings window.
            ApplyLayout();

            if (DeskConfigStore.SameRuntime(local, merged))
            {
                _log.LogDebug("Desk updated by {Host}", sourceHost);
                Changed?.Invoke();
                return;
            }
            _log.LogInformation("Who holds the keyboard changed ({Host}); restarting into the new role", sourceHost);
            ScheduleRestart();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Ignoring an unusable desk document from {Host}", sourceHost); }
    }

    private DeskActionResult Forward(DeskCommandMessage command)
    {
        var controller = _snapshot.Controller;
        if (string.IsNullOrWhiteSpace(controller) || controller.Equals(LocalName, StringComparison.OrdinalIgnoreCase))
            return DeskActionResult.Fail("The computer that controls the desk is not connected right now.");
        Send([controller], MessageSerializer.Encode(MessageKind.DeskCommand, command));
        return DeskActionResult.Ok($"Asked {controller} to do it.");
    }

    // Before the first peer list arrives, the masters the relay already knows about are the best
    // guess at who is listening — a joining computer should not have to wait a round to be seen.
    // Takes the round's cancellation, because it can wait indefinitely.
    //
    // Asking who the controller is takes a lock shared with the relay, and a follower whose
    // controller is not there can sit on it forever. Without a token the round never returns, the
    // four-second loop never ticks again, and the desk keeps the snapshot it started with -- an
    // empty one. What the user sees is a desk with no monitors and no explanation, on a machine
    // whose config is perfectly fine.
    private async Task SendToControllerAsync(byte[] payload, CancellationToken cancel)
    {
        var targets = _peers;
        if (targets.Length == 0) targets = await _world.GetMasters().AsTask().WaitAsync(cancel);
        if (targets.Length == 0) return;
        Send(targets, payload);
    }

    private void Send(string[] targets, byte[] payload)
    {
        if (targets.Length == 0 || !_relay.IsConnected) return;
        try { _relay.Send(targets, payload); }
        catch (Exception ex) { _log.LogDebug(ex, "Desk message could not be sent"); }
    }

    private void ScheduleRestart()
    {
        if (Interlocked.Exchange(ref _restartScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(_restartDelay);
            _restart();
        });
    }

    // A desk document that cannot be read is not an empty desk.
    //
    // Swallowing the failure and carrying on with a blank document made an unreadable config look
    // exactly like a fresh install: no monitors, no other computer, no crossings, and not one word
    // anywhere to say why. Worse, the blank was then written back over the file that could not be
    // read, so a config the desk merely failed to parse became a config that really was empty --
    // taking the arrangement, the pairing and the learned monitor wiring with it.
    private HydraConfigFile SafeLoad()
    {
        try
        {
            var loaded = _store.Load();
            _configUnreadable = false;
            return loaded;
        }
        catch (Exception ex)
        {
            _configUnreadable = true;
            _log.LogError(ex, "Could not read the desk document at {Path} -- starting with an empty desk "
                + "and refusing to write over it. Nothing will be lost, but nothing will work either "
                + "until this is fixed", _store.Path);
            return new HydraConfigFile();
        }
    }

    // Set while the document on disk could not be read. Everything else carries on; only writing is
    // held back, because the one thing worse than an unreadable desk is overwriting it.
    private bool _configUnreadable;
}
