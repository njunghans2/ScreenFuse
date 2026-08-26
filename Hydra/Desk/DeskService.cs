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
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DeskInventoryMessage>> _inventoryPending = new(StringComparer.OrdinalIgnoreCase);
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

        _log.LogDebug("Local inventory: {Monitors}",
            string.Join("; ", monitors.Select(m => $"{m.Description}({m.DdcId})={m.CurrentInput?.ToString() ?? "null"}")));

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
            // its purpose and the real reports take over. A guess the reports contradict — the
            // switch never held — must not linger either, or the desk keeps claiming a computer
            // that is not on the monitor.
            foreach (var (id, host) in _optimistic.ToList())
            {
                var view = merge.Views.FirstOrDefault(v => v.Id == id);
                if (view == null) continue;
                if (view.Sources.Any(s => s.Reachable && s.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
                    || (view.ActiveHost != null && !view.ActiveHost.Equals(host, StringComparison.OrdinalIgnoreCase)))
                    _optimistic.Remove(id);
            }

            _log.LogDebug("Desk recompute: merged into {Count} monitor(s)", merge.Views.Count);
            _log.LogDebug("Merge result: {Views}", string.Join("; ", merge.Views.Select(v =>
                $"{v.Label}=>{v.ActiveHost ?? "none"} [{string.Join(",", v.Sources.Select(s => $"{s.Host}:{s.Input?.ToString() ?? "?"}"))}]")));
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
                m.Sources.Select(s => new DeskStateSource(s.Host, s.Input, s.Reachable)).ToList(),
                m.Sleeping)).ToList(),
            _snapshot.Scenes.ToList(),
            _snapshot.CurrentScene,
            _controllerStore.Read());
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
        // control today, and each machine changes role where it stands.
        Send(_peers, MessageSerializer.Encode(MessageKind.DeskCommand, new DeskCommandMessage(DeskCommandKind.SetController, Host: host)));
        ApplyController(host);
        await Task.CompletedTask;
        return DeskActionResult.Ok($"{host} now has the keyboard and mouse.");
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


    // Codes worth trying: the ones the monitor admits to, minus any already spoken for by another
    // computer. Trying a code someone else owns can only ever prove what is already known.
    private static List<int> Candidates(DeskMonitorConfig monitor, string host)
    {
        var taken = monitor.Sources
            .Where(s => !s.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && s.Input != null)
            .Select(s => s.Input!.Value)
            .ToHashSet();

        return [.. monitor.Sources
            .SelectMany(s => s.AvailableInputs)
            .Distinct()
            .Where(i => !taken.Contains(i))
            .OrderBy(i => i)];
    }

    private async Task RevertAsync(string owner, string ddcId, int? original)
    {
        if (original == null) return;
        try
        {
            using var cancel = new CancellationTokenSource(RemoteTimeout);
            await SwitchAsync(owner, ddcId, original.Value, cancel.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not put the monitor back on input {Input}", original);
        }
    }

    private async Task RecordInputAsync(string monitorId, string host, int input)
    {
        await _gate.WaitAsync();
        try
        {
            _config = WithMonitors(_config, _config.Monitors
                .Select(m => m.Id != monitorId ? m : m.With(sources: Upsert(m.Sources, host, input)))
                .ToList());
            await PersistAsync(push: true);
        }
        finally { _gate.Release(); }
        await RecomputeAsync(CancellationToken.None);
    }

    // MCCS assigns these meanings to VCP 0x60. The code stays in the name because a monitor's idea
    // of "HDMI 1" and the socket printed on its back do not always agree.
    private static string InputName(int code) => code switch
    {
        1 => "VGA 1 (1)",
        3 => "DVI 1 (3)",
        4 => "DVI 2 (4)",
        15 => "DisplayPort 1 (15)",
        16 => "DisplayPort 2 (16)",
        17 => "HDMI 1 (17)",
        18 => "HDMI 2 (18)",
        _ => $"input {code}",
    };

    public Task<DeskActionResult> SaveArrangementAsync(IReadOnlyList<DeskPlacement> placements, CancellationToken cancellationToken = default) =>
        IsController
            ? SaveArrangementCoreAsync(placements)
            : Task.FromResult(Forward(new DeskCommandMessage(DeskCommandKind.SaveArrangement,
                Arrangement: placements.Select(p => new DeskArrangementEntry(p.Monitor, p.DeskX, p.DeskY, p.Width, p.Height, p.Label)).ToList())));

    // Troubleshooting: bring every display on the desk back up. Wakes this computer's displays and
    // asks every connected peer to do the same, so a monitor that drifted to sleep or lost its
    // signal can re-lock onto the computer that should be driving it. Runs on either role; a
    // follower forwards it to the controller.
    public async Task<DeskActionResult> WakeAllDisplaysAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var targets = new List<string> { LocalName };
            targets.AddRange(_peers.Where(p => !p.Equals(LocalName, StringComparison.OrdinalIgnoreCase)));
            await Task.WhenAll(targets.Select(t => WakeForSwitchAsync(t, cancellationToken)).ToArray());
            return DeskActionResult.Ok($"Woke the displays on {string.Join(", ", targets)}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DeskActionResult.Fail("Cancelled.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not wake all displays");
            return DeskActionResult.Fail($"Could not wake all displays: {ex.Message}");
        }
    }

    // Troubleshooting: ask every connected peer to make its cursor visible again. A stranded
    // pointer leaves a computer with a hidden cursor and no input that can recover it, so this
    // gives the user a way back. Best effort: a peer that is away simply does not answer.
    public Task<DeskActionResult> ResetCursorsAsync(CancellationToken cancellationToken = default)
    {
        Send(_peers, MessageSerializer.Encode(MessageKind.CursorReset, new CursorResetMessage()));
        return Task.FromResult(DeskActionResult.Ok("Cursor reset requested on the connected computers."));
    }

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
        if (!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(ddcId))
        {
            // Wake the computer we are switching *to* first, and give it a moment.
            //
            // A monitor asked for an input nothing is driving finds no signal and goes hunting for
            // one, which lands it straight back on the computer it just left -- a switch that goes
            // black and undoes itself a few seconds later. The same switch works perfectly when that
            // computer happens to be awake, which is exactly what makes it look intermittent rather
            // than like the missing step it is.
            await WakeForSwitchAsync(host, cancel);

            // …and put its display for *this* monitor back on its desktop before the input moves,
            // not after. Waking a computer is not the same as it driving the panel: a display that
            // was removed from the desktop stays removed, so the monitor still arrived at a socket
            // with no signal on it. This used to run at the end of the switch, several seconds
            // after the monitor had already given up and hunted back.
            await PrepareGainingDisplayAsync(monitor, host, cancel);

            if (target?.Input is { } input)
            {
                result = await SwitchAsync(owner!, ddcId!, input, cancel);
                if (!result.Accepted) result = ExplainSwitchGap(monitor, owner, host, result.Message);
            }
            else
            {
                result = await DiscoverAndSwitchAsync(monitor, owner!, ddcId!, host, cancel);
            }
        }
        else result = ExplainSwitchGap(monitor, owner, host, null);
        if (!result.Accepted) return result;

        // The user's choice lands on the desk right away: the optimistic assignment and the state
        // broadcast go out now, not after the switch has been nursed through its settling. If the
        // switch later proves not to have held, this is undone below — but the peer never sits on a
        // stale selection while the DDC commands run.
        _optimistic[monitorId] = host;
        _snapshot = BuildSnapshot(_snapshot.Monitors
            .Select(m => m.Id == monitorId ? m with { ActiveHost = host } : m).ToList());
        Broadcast();
        Changed?.Invoke();

        // A monitor switching inputs toggles its hot-plug line for a moment, and macOS reads that
        // as a physical unplug: the display drops out of its arrangement, the Mac stops driving it,
        // and the monitor — seeing no signal — hunts straight back to the computer it just left.
        // Reconnect the target's displays after the switch, and if the monitor still reverted,
        // re-assert the switch until it holds.
var held = true;
        if (!host.Equals(LocalName, StringComparison.OrdinalIgnoreCase))
        {
            await WaitForScreenAsync(host, monitor, cancel);
            if (target?.Input is { } input)
                held = await EnsureSwitchHeldAsync(owner!, ddcId!, monitor, host, input, cancel);
        }

        if (!held)
        {
            // The switch was issued but did not stick — the monitor hunted back. Say so, and put
            // the desk back on the truth instead of leaving a phantom "shows Mac" on both peers.
            _optimistic.Remove(monitorId);
            await RecomputeAsync(CancellationToken.None);
            _log.LogWarning("{Monitor} did not stay on {Host} — the monitor reverted", monitor.DisplayName(), host);
            return DeskActionResult.Fail($"{monitor.DisplayName()} did not stay on {host} — the monitor switched away again. It is back on the computer that was driving it.");
        }

        // A monitor that was blanked as some computer's last display is moving to a live one now:
        // the input change itself wakes its panel, and its crossings return. Done before the
        // topology below, so a monitor blanked *by* this very switch keeps its mark.
        await _gate.WaitAsync(cancel);
        try
        {
            if (_config.Monitors.FirstOrDefault(m => m.Id == monitorId) is { Sleeping: true })
            {
                _config = WithMonitors(_config, _config.Monitors
                    .Select(m => m.Id != monitorId ? m : m.With(sleeping: false))
                    .ToList());
            }
        }
        finally { _gate.Release(); }

        // The monitor now shows another computer, so every computer it is wired to but no longer
        // shows must drop the display from its desktop — otherwise windows and the pointer keep
        // living on the invisible panel. The gaining side was seen to before the switch; this is
        // the losers, and it deliberately runs after the hold loop above: dropping a display takes
        // its DDC channel with it, and the loser is the computer that commands this monitor.
        await ReleaseLosingDisplaysAsync(monitor, host, cancel);

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

        // The optimistic view above predates the topology: a display may have been blanked (the
        // last one of a computer) or the crossings moved. The desk the peers see must match the
        // document, so the snapshot is rebuilt from it before it is broadcast.
        _snapshot = BuildSnapshot(_config.Monitors.Select(ToView).ToList());

        Broadcast();
        Changed?.Invoke();
        return DeskActionResult.Ok($"{monitor.DisplayName()} switched to {host}.");
    }

    // One monitor's view from the document, with the live parts (which computer is on it, and
    // which sources are reachable) carried over from the current picture.
    private DeskMonitorView ToView(DeskMonitorConfig m)
    {
        var current = _snapshot.Monitors.FirstOrDefault(v => v.Id == m.Id);
        return new DeskMonitorView(
            m.Id, m.DisplayName(), m.DeskX, m.DeskY, m.Width, m.Height,
            current?.ActiveHost, current?.Sources ?? [], m.Sleeping);
    }

    private async Task ApplyTopologyForSwitchAsync(DeskMonitorConfig monitor, string gainingHost, CancellationToken cancel)
    {
        foreach (var source in monitor.Sources)
        {
            if (source.Host.Equals(gainingHost, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(source.DdcId)) continue;

            // What would stay on this host after the switch. Two special cases, and they are the
            // point of this method's existence:
            //  - nothing would stay: the switched monitor is its last display, and the panel is
            //    blanked instead of removed — an OS with no display to render is a soft-locked OS;
            //  - only unswitchable displays would stay (a laptop panel wired to nothing else): those
            //    keep the host alive, and they too are blanked rather than left lit — the user asked
            //    for the computer to be off.
            var remaining = _snapshot.Monitors.Where(v =>
                string.Equals(v.ActiveHost, source.Host, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(v.Id, monitor.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            var blank = remaining.Count == 0
                ? new[] { monitor }
                : remaining.All(v => v.Sources.Count <= 1) ? remaining.Select(v => _config.Monitors.FirstOrDefault(m => m.Id == v.Id)!).Where(m => m != null).ToArray() : [];

            if (source.Host.Equals(LocalName, StringComparison.OrdinalIgnoreCase))
            {
                if (blank.Any(b => b.Id == monitor.Id))
                {
                    var result = await _router.SetDisplayStandbyAsync(source.DdcId, standby: true, cancel);
                    _log.LogInformation("Last display for {Host}: blanked {DdcId} instead of disabling ({Success} {Detail})",
                        LocalName, source.DdcId, result.Success, result.Detail);
                    foreach (var keep in blank) await MarkSleepingAsync(keep.Id);
                    continue;
                }
                var disabled = await _router.SetMonitorDisplayEnabledAsync(source.DdcId, enabled: false, cancel);
                _log.LogInformation("Disabled local display {DdcId} for {Monitor}: {Success} {Detail}",
                    source.DdcId, monitor.DisplayName(), disabled.Success, disabled.Detail);
            }
            else if (_peers.Contains(source.Host, StringComparer.OrdinalIgnoreCase))
            {
                if (blank.Any(b => b.Id == monitor.Id))
                {
                    Send([source.Host], MessageSerializer.Encode(MessageKind.SetMonitorStandby,
                        new SetMonitorStandbyMessage(source.DdcId, Standby: true)));
                    _log.LogInformation("Asked {Host} to blank its last display {DdcId}", source.Host, source.DdcId);
                    foreach (var keep in blank) await MarkSleepingAsync(keep.Id);
                    continue;
                }
                Send([source.Host], MessageSerializer.Encode(MessageKind.SetMonitorDisplay,
                    new SetMonitorDisplayMessage(source.DdcId, Enabled: false)));
                _log.LogInformation("Asked {Host} to disable its display {DdcId} for {Monitor}",
                    source.Host, source.DdcId, monitor.DisplayName());
                foreach (var keep in blank)
                {
                    if (keep.Source(source.Host) is { DdcId: not null } keepSource)
                        Send([source.Host], MessageSerializer.Encode(MessageKind.SetMonitorStandby,
                            new SetMonitorStandbyMessage(keepSource.DdcId, Standby: true)));
                    await MarkSleepingAsync(keep.Id);
                }
            }
        }

        var gain = monitor.Source(gainingHost);
        if (gainingHost.Equals(LocalName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(gain?.DdcId))
        {
            var result = await _router.SetMonitorDisplayEnabledAsync(gain.DdcId, enabled: true, cancel);
            _log.LogInformation("Re-enabled local display {DdcId} for {Monitor}: {Success} {Detail}",
                gain.DdcId, monitor.DisplayName(), result.Success, result.Detail);
        }
    }

    // Records that one monitor is the blanked last display of its computer: the crossing edges are
    // cut (a pointer must never enter a black panel), and the state is persisted and shared.
    private async Task MarkSleepingAsync(string monitorId)
    {
        await _gate.WaitAsync();
        try
        {
            var monitors = _config.Monitors
                .Select(m => m.Id != monitorId ? m : m.With(sleeping: true))
                .ToList();
            _config = WithMonitors(_config, monitors);
            _config = RebuildHosts(_config);
            ApplyLayout();
            await PersistAsync(push: true);
        }
        finally { _gate.Release(); }
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
                // Fire-and-forget: the wake is best effort and the switch must not stall behind a
                // busy peer (a machine re-enumerating its displays can take a while to answer).
                // The settle below gives the request time to land before the monitor is switched.
                Send([host], MessageSerializer.Encode(MessageKind.DeskDisplayPower,
                    new DeskDisplayPowerMessage(Guid.NewGuid().ToString("N"), Wake: true)));
            }

            // Output does not come back the instant it is asked for. Switching into a signal that is
            // still arriving is the same as switching into no signal at all.
            var settle = _config.Profiles.FirstOrDefault()?.DisplayRouting.SettleDelayMs ?? 500;
            if (settle > 0) await Task.Delay(settle, cancel);

            // A monitor arriving means the desk is coming back to this computer, so any display
            // that was blanked as its last one wakes: the panel comes back, the crossing edges
            // return, and the computer is fully itself again.
            await _gate.WaitAsync(cancel);
            try
            {
                var slept = _config.Monitors.Where(m => m.Sleeping
                    && string.Equals(_snapshot.Monitors.FirstOrDefault(v => v.Id == m.Id)?.ActiveHost, host, StringComparison.OrdinalIgnoreCase)).ToList();
                if (slept.Count > 0)
                {
                    var monitors = _config.Monitors
                        .Select(m => !slept.Any(s => s.Id == m.Id) ? m : m.With(sleeping: false))
                        .ToList();
                    _config = WithMonitors(_config, monitors);
                    _config = RebuildHosts(_config);
                    ApplyLayout();
                    await PersistAsync(push: true);
                    foreach (var m in slept)
                    {
                        var source = m.Source(host)?.DdcId;
                        if (source == null) continue;
                        if (host.Equals(LocalName, StringComparison.OrdinalIgnoreCase))
                            await _router.SetDisplayStandbyAsync(source, standby: false, cancel);
                        else
                            Send([host], MessageSerializer.Encode(MessageKind.SetMonitorStandby,
                                new SetMonitorStandbyMessage(source, Standby: false)));
                    }
                    _log.LogInformation("Woke {Count} blanked display(s) on {Host}", slept.Count, host);
                }
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Could not wake {Host} before switching a monitor to it", host);
        }
    }

    // An unknown cable is probed automatically only when a changed display inventory can prove the
    // target gained the panel. Some display stacks retain inactive panels, so a plain VCP read is
    // deliberately not enough evidence: accepting that would permanently save the first arbitrary
    // socket we tried.
    private async Task<DeskActionResult> DiscoverAndSwitchAsync(
        DeskMonitorConfig monitor, string owner, string ddcId, string target, CancellationToken cancel)
    {
        if (!target.Equals(LocalName, StringComparison.OrdinalIgnoreCase)
            && !_peers.Contains(target, StringComparer.OrdinalIgnoreCase))
            return DeskActionResult.Fail($"{target} is disconnected, so ScreenFuse will wait to discover its input on {monitor.DisplayName()}.");

        var candidates = Candidates(monitor, target);
        if (candidates.Count == 0)
            return DeskActionResult.Fail($"ScreenFuse has no unassigned input codes to try for {target} on {monitor.DisplayName()}.");

        var original = monitor.Source(owner)?.Input;
        var discovered = false;
        try
        {
            foreach (var candidate in candidates)
            {
                var switched = await SwitchAsync(owner, ddcId, candidate, cancel);
                if (!switched.Accepted) continue;

                // The monitor's input change makes its hot-plug line drop for a moment and macOS
                // reads that as an unplug, so the target's display for this monitor has to be
                // reconnected — otherwise the candidate has no signal to show. Then verify by
                // reading the monitor's current input back from the computer that can command it:
                // the monitor accepted the candidate only if it now reports it. (The target's own
                // screen list is not proof — macOS lists a connected panel whether or not the
                // monitor is displaying it.)
                if (await WaitForInputAsync(owner, monitor, target, candidate, cancel))
                {
                    discovered = true;
                    await RecordInputAsync(monitor.Id, target, candidate);
                    return DeskActionResult.Ok($"{monitor.DisplayName()} automatically found {target} on {InputName(candidate)}.");
                }
            }

            return DeskActionResult.Fail($"ScreenFuse could not verify an input for {target} on {monitor.DisplayName()} yet; it will retry automatically when that display reconnects.");
        }
        finally
        {
            if (!discovered) await RevertAsync(owner, ddcId, original);
        }
    }

    private async Task<bool> WaitForInputAsync(string owner, DeskMonitorConfig monitor, string target, int candidate, CancellationToken cancel)
    {
        var settle = _config.Profiles.FirstOrDefault()?.DisplayRouting.SettleDelayMs ?? 500;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (settle > 0 && attempt == 0) await Task.Delay(settle, cancel);
            // Reconnect the target's displays — the monitor's input change makes its hot-plug line
            // drop for a moment, and macOS reads that as an unplug. The drop can lag the switch, so
            // keep re-asking until the monitor actually reports the candidate input.
            await WakeForSwitchAsync(target, cancel);
            var inventory = await RequestInventoryAsync(owner, cancel);
            if (CurrentInputOf(inventory, monitor) == candidate) return true;
            if (attempt < 5) await Task.Delay(750, cancel);
        }
        return false;
    }

    private static int? CurrentInputOf(DeskInventoryMessage? inventory, DeskMonitorConfig monitor)
    {
        if (inventory == null) return null;
        string?[] names = [monitor.Label ?? monitor.Id, .. monitor.Aliases];
        var report = inventory.Monitors.FirstOrDefault(r => DeskMerge.SameMonitor(
            names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!),
            new[] { r.Description, r.DdcId }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!)));
        return report?.CurrentInput;
    }

    // Whether the target's display server currently lists the monitor as one of its screens — i.e.
    // the display is back in its arrangement. The monitor's own input change makes its hot-plug line
    // drop for a moment, and macOS reads that as an unplug and removes the display, so the target is
    // re-asked to reconnect its displays before every check, and the checks continue for a few
    // seconds (the display server polls its screens every couple of seconds).
    private async Task<bool> WaitForScreenAsync(string host, DeskMonitorConfig monitor, CancellationToken cancel)
    {
        var settle = _config.Profiles.FirstOrDefault()?.DisplayRouting.SettleDelayMs ?? 500;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (settle > 0 && attempt == 0) await Task.Delay(settle, cancel);
            // Reconnect the target's displays — the monitor's input change makes its hot-plug line
            // drop for a moment, and macOS reads that as an unplug and removes the display. The
            // drop can lag the switch, so keep re-asking until the display is back in the
            // arrangement (the display server polls its screens every couple of seconds).
            await WakeForSwitchAsync(host, cancel);
            var inventory = await RequestInventoryAsync(host, cancel);
            if (inventory != null && ReportsScreen(inventory, monitor)) return true;
            if (attempt < 5) await Task.Delay(700, cancel);
        }
        return false;
    }

    private static bool ReportsScreen(DeskInventoryMessage inventory, DeskMonitorConfig monitor)
    {
        string?[] names = [monitor.Label ?? monitor.Id, .. monitor.Aliases];
        return inventory.Screens.Any(screen => DeskMerge.SameMonitor(
            names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!),
            new[] { screen.DisplayName, screen.ScreenId }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!)));
    }

    // A switched monitor can quietly revert: its input change drops its hot-plug line for a
    // moment, macOS removes the display, and the monitor hunts back to the signal it can still
    // see. Read the monitor's input back from the computer that commands it, and re-assert the
    // switch (waking the target first) until it actually holds.
    private async Task<bool> EnsureSwitchHeldAsync(string owner, string ddcId, DeskMonitorConfig monitor, string target, int input, CancellationToken cancel)
    {
        // The switch's hot-plug blip makes macOS drop the display, and the monitor hunts for a
        // signal it does not see within a couple of seconds. Wake aggressively — every ~400ms —
        // so the display is back on the target before the hunt begins; re-assert the switch only
        // if the monitor has already moved away.
        var known = monitor.Sources.SelectMany(s => s.AvailableInputs).Distinct().ToList();
        int? lastHonest = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await WakeForSwitchAsync(target, cancel);
            await Task.Delay(300, cancel);
            var inventory = await RequestInventoryAsync(owner, cancel);
            var current = CurrentInputOf(inventory, monitor);
            _log.LogInformation("Switch hold check {Attempt}: {Monitor} reads input {Current}, want {Want}",
                attempt + 1, monitor.DisplayName(), current?.ToString() ?? "null", input);
            if (current == input) return true;
            // A reading the monitor does not admit to — the helper answered another panel, or the
            // monitor's DDC is wedged mid-scan — cannot disprove the switch, so it is not treated
            // as evidence the monitor moved. Only an honest reading of a different socket is.
            if (current is { } c && (known.Count == 0 || known.Contains(c))) lastHonest = c;
            var result = await SwitchAsync(owner, ddcId, input, cancel);
            if (!result.Accepted) return false;
        }
        // Every reading was nonsense the monitor does not admit to: there is no evidence the
        // switch failed, and reverting would send the panel back to the dead socket it came from.
        return lastHonest == null;
    }

    private async Task<DeskInventoryMessage?> RequestInventoryAsync(string host, CancellationToken cancel)
    {
        if (host.Equals(LocalName, StringComparison.OrdinalIgnoreCase)) return await BuildInventoryAsync(cancel);

        var completion = new TaskCompletionSource<DeskInventoryMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inventoryPending.TryAdd(host, completion)) return null;
        try
        {
            Send([host], MessageSerializer.Encode(MessageKind.DeskInventoryRequest, new DeskInventoryRequestMessage()));
            return await completion.Task.WaitAsync(RemoteTimeout, cancel);
        }
        catch (TimeoutException) { return null; }
        finally { _inventoryPending.TryRemove(host, out _); }
    }

    // Explains why a normal switch could not start.
    private DeskActionResult ExplainSwitchGap(DeskMonitorConfig monitor, string? owner, string host, string? ddcFailure)
    {
        if (host.Equals(owner, StringComparison.OrdinalIgnoreCase))
            return DeskActionResult.Ok($"{monitor.DisplayName()} already shows {host}.");
        if (ddcFailure != null)
            return DeskActionResult.Fail($"{monitor.DisplayName()} refused the input switch: {ddcFailure}");
        if (string.IsNullOrWhiteSpace(owner))
            return DeskActionResult.Fail($"No connected computer can reach {monitor.DisplayName()} right now, so its input cannot be switched.");

        return DeskActionResult.Fail($"ScreenFuse cannot reach {monitor.DisplayName()} to switch it to {host}.");
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
        var gone = _peers.Except(peers, StringComparer.OrdinalIgnoreCase).ToArray();
        _peers = peers;
        foreach (var missing in _reports.Keys.Where(k => !k.Equals(LocalName, StringComparison.OrdinalIgnoreCase) && !peers.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            _reports.TryRemove(missing, out _);
        _replied.RemoveWhere(h => !peers.Contains(h, StringComparer.OrdinalIgnoreCase));
        // Optimistic ownership only bridges the short interval between a DDC command and the next
        // inventory. Retaining it after a peer disappears leaves crossings pointing at a powered-off
        // desktop, which in turn strands windows on a display nobody can see.
        foreach (var monitor in _optimistic.Where(pair => gone.Contains(pair.Value, StringComparer.OrdinalIgnoreCase)).Select(pair => pair.Key).ToList())
            _optimistic.Remove(monitor);
        Greet(added);
        return IsController && gone.Length > 0
            ? RecomputeAsync(CancellationToken.None)
            : NotifyChangedAsync();
    }

    private Task NotifyChangedAsync()
    {
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    private async Task OnMessageReceived(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        if (kind is MessageKind.DeskInventory or MessageKind.DeskState or MessageKind.DeskCommand
            or MessageKind.DeskConfigPush or MessageKind.DeskSetInput or MessageKind.DeskSetInputResult
            or MessageKind.DeskDisplayPower or MessageKind.DeskInventoryRequest)
        {
            _replied.Add(sourceHost);
            _log.LogDebug("Desk message {Kind} from {Host} (controller={IsController})", kind, sourceHost, IsController);
        }

        switch (kind)
        {
            case MessageKind.DeskInventory when IsController:
            {
                var inventory = new DecodedMessage(kind, body).Deserialize<DeskInventoryMessage>();
                _log.LogDebug("Inventory from {Host}: {Monitors} screens={Screens}",
                    sourceHost,
                    string.Join("; ", inventory.Monitors.Select(m => $"{m.Description}={m.CurrentInput?.ToString() ?? "null"}")),
                    string.Join("; ", inventory.Screens.Select(s => $"{s.DisplayName ?? s.ScreenId}")));
                _reports[sourceHost] = inventory;
                if (_inventoryPending.TryGetValue(sourceHost, out var completion)) completion.TrySetResult(inventory);
                await RecomputeAsync(CancellationToken.None);
                break;
            }

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

            case MessageKind.DeskInventoryRequest:
                Send([sourceHost], MessageSerializer.Encode(MessageKind.DeskInventory, await BuildInventoryAsync(CancellationToken.None)));
                break;

            case MessageKind.SetMonitorDisplay:
            {
                var request = new DecodedMessage(kind, body).Deserialize<SetMonitorDisplayMessage>();
                var result = await _router.SetMonitorDisplayEnabledAsync(request.LocalSourceId, request.Enabled);
                if (!result.Success)
                    _log.LogDebug("Could not {Action} the display for {Source}: {Detail}",
                        request.Enabled ? "re-enable" : "disable", request.LocalSourceId, result.Detail);
                break;
            }

            case MessageKind.SetMonitorStandby:
            {
                var request = new DecodedMessage(kind, body).Deserialize<SetMonitorStandbyMessage>();
                var result = await _router.SetDisplayStandbyAsync(request.LocalSourceId, request.Standby);
                _log.LogInformation("Display standby {Standby} for {Source}: {Success} {Detail}",
                    request.Standby ? "on" : "off", request.LocalSourceId, result.Success, result.Detail);
                break;
            }

            case MessageKind.DeskDisplayPower:
            {
                var request = new DecodedMessage(kind, body).Deserialize<DeskDisplayPowerMessage>();
                _log.LogInformation("Display power request from {Host}: wake={Wake}", sourceHost, request.Wake);
                var result = await _router.SetDisplayPowerAsync(request.Wake);
                _log.LogInformation("Display power {Action} on this computer: {Success} {Detail}",
                    request.Wake ? "wake" : "sleep", result.Success, result.Detail);
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

                // The controller's hand-taken control travels with the state, so a machine that
                // missed the handover command still ends up agreeing on the next broadcast —
                // the configuration must be the same on every computer.
                try
                {
                    if (state.ControllerOverride is { } overrideHost)
                        _controllerStore.Write(overrideHost);
                    else
                        _controllerStore.Clear();
                }
                catch (Exception ex) { _log.LogDebug(ex, "Could not mirror the controller override"); }

                // The controller travels with the picture, so a computer that missed the handover
                // command agrees on the next broadcast — including when the name it reads is its
                // own, which is how a machine finds out it has just been handed control. The
                // override above is already on disk, so this only has to change the role.
                ApplyControllerLive(state.Controller);
                _controller = state.Controller;

                // If that name was our own we are the controller as of this instant, and the
                // follower's picture below — which says plainly that it is not — must not be built
                // over the top of it. The next round is ours to broadcast.
                if (IsController) break;

                _snapshot = new DeskSnapshot(
                    state.Controller, LocalName, state.Hosts, state.ConnectedHosts,
                    state.Monitors.Select(m => new DeskMonitorView(
                        m.Id, m.Label, m.DeskX, m.DeskY, m.Width, m.Height, m.ActiveHost,
                        m.Sources.Select(s => new DeskSourceView(s.Host, s.Input, s.Reachable)).ToList(),
                        m.Sleeping)).ToList(),
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
            DeskCommandKind.SaveArrangement => await SaveArrangementCoreAsync(
                (command.Arrangement ?? []).Select(a => new DeskPlacement(a.Monitor, a.DeskX, a.DeskY, a.Width, a.Height, a.Label)).ToList()),
            _ => DeskActionResult.Fail($"Unsupported desk command {command.Kind}."),
        };
        if (!result.Accepted) _log.LogWarning("Desk command {Kind} from {Host} refused: {Message}", command.Kind, sourceHost, result.Message);
    }

    // Handing the keyboard over used to restart every agent into its new role. It was the only
    // way: the process picked its role at startup and built one half of the desk around it, so
    // there was nothing to change over. Both halves run on every computer now, and this is the
    // whole of the handover — write it down, and say so.
    private void ApplyController(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Equals(_controller, StringComparison.OrdinalIgnoreCase)) return;

        // On disk first. A computer restarted for any other reason has to come back in the role it
        // was in, not the one its config file was written with — the override is what outranks the
        // controller the scene names, and it is the only record of a choice made by hand.
        try { _controllerStore.Write(host); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not record the new controller");
            return;
        }
        ApplyControllerLive(host);
    }

    // The role change on its own, for the paths that have already settled where it is written down
    // — a follower reading the controller off the broadcast picture has had its override mirrored
    // for it a moment earlier, and must not write a second, different answer over the top.
    private void ApplyControllerLive(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Equals(_controller, StringComparison.OrdinalIgnoreCase)) return;
        _controller = host;
        _profile.ApplyController(host);
        _snapshot = _snapshot with { Controller = host, IsController = IsController };
        _log.LogInformation("The keyboard and mouse moved to {Host}", host);
        Broadcast();
        Changed?.Invoke();
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

            // Who holds the keyboard is deliberately NOT taken from here. A document still names
            // whichever computer it was written with, and control handed over by hand outranks
            // that — reading it back out of the document would hand control straight back to the
            // machine that just gave it up. The handover travels as its own command, and as the
            // controller name on every broadcast picture.
            _log.LogDebug("Desk updated by {Host}", sourceHost);
            Changed?.Invoke();
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
