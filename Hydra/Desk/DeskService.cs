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

    protected override async Task Execute(CancellationToken cancel)
    {
        var inventory = await BuildInventoryAsync(cancel);
        if (IsController)
        {
            _reports[LocalName] = inventory;
            await RecomputeAsync(cancel);
        }
        else
        {
            await SendToControllerAsync(MessageSerializer.Encode(MessageKind.DeskInventory, inventory));
        }
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
        await _gate.WaitAsync(cancel);
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

            _snapshot = BuildSnapshot(merge.Views);
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
            IsController: IsController);
    }

    private void Broadcast()
    {
        if (!IsController || _peers.Length == 0) return;
        var message = new DeskStateMessage(
            _snapshot.Controller,
            _snapshot.Hosts.ToList(),
            _snapshot.ConnectedHosts.ToList(),
            _snapshot.Monitors.Select(m => new DeskStateMonitor(
                m.Id, m.Label, m.DeskX, m.DeskY, m.Width, m.Height, m.ActiveHost,
                m.Sources.Select(s => new DeskStateSource(s.Host, s.Input, s.Reachable)).ToList())).ToList(),
            _snapshot.Scenes.ToList(),
            _snapshot.CurrentScene);
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

        var target = monitor.Source(host);
        if (target?.Input == null)
            return DeskActionResult.Fail(
                $"ScreenFuse does not know which input on {monitor.DisplayName()} shows {host}. Set it under 'How each computer is wired'.");

        // Only the computer currently on the monitor can talk to it, so the switch is delegated —
        // and it has to be the computer that actually reported the monitor this round, not the one
        // an earlier switch optimistically put there.
        var owner = view?.Sources.FirstOrDefault(s => s.Reachable)?.Host ?? view?.ActiveHost;
        if (string.IsNullOrWhiteSpace(owner))
            return DeskActionResult.Fail($"No connected computer can reach {monitor.DisplayName()} right now, so its input cannot be switched.");
        var ddcId = monitor.Source(owner!)?.DdcId;
        if (string.IsNullOrWhiteSpace(ddcId))
            return DeskActionResult.Fail($"{owner} has no DDC address for {monitor.DisplayName()}.");

        var result = await SwitchAsync(owner!, ddcId!, target.Input.Value, cancel);
        if (!result.Accepted) return result;

        _optimistic[monitorId] = host;
        _snapshot = BuildSnapshot(_snapshot.Monitors
            .Select(m => m.Id == monitorId ? m with { ActiveHost = host } : m).ToList());
        Broadcast();
        Changed?.Invoke();
        return DeskActionResult.Ok($"{monitor.DisplayName()} switched to {host}.");
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
            _config = WithMonitors(_config, _config.Monitors.Select(m => m.Id != monitorId ? m : new DeskMonitorConfig
            {
                Id = m.Id, Label = m.Label, DeskX = m.DeskX, DeskY = m.DeskY, Width = m.Width, Height = m.Height,
                Sources = Upsert(m.Sources, host, input),
            }).ToList());
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
        result.Add(new MonitorSourceConfig { Host = host, Input = input, DdcId = existing?.DdcId, ScreenId = existing?.ScreenId });
        return result;
    }

    private async Task<DeskActionResult> SaveArrangementCoreAsync(IReadOnlyList<DeskPlacement> placements)
    {
        await _gate.WaitAsync();
        try
        {
            var byId = placements.ToDictionary(p => p.Monitor, StringComparer.OrdinalIgnoreCase);
            var monitors = _config.Monitors.Select(m => !byId.TryGetValue(m.Id, out var p) ? m : new DeskMonitorConfig
            {
                Id = m.Id,
                Label = string.IsNullOrWhiteSpace(p.Label) ? m.Label : p.Label,
                DeskX = p.DeskX, DeskY = p.DeskY,
                Width = p.Width > 0 ? p.Width : m.Width,
                Height = p.Height > 0 ? p.Height : m.Height,
                Sources = m.Sources,
            }).ToList();

            _config = WithMonitors(_config, monitors);
            _config = RebuildHosts(_config);
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
                ?? _snapshot.Monitors.FirstOrDefault(m => m.Id == id)?.ActiveHost);
            return Clone(profile, DeskArrangement.BuildHosts(placed, allHosts));
        }).ToList();
        return WithProfiles(file, profiles);
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
        if (IsController && added.Length > 0)
        {
            // A computer that just joined needs the desk document before it can show anything useful.
            try { Send(added, MessageSerializer.Encode(MessageKind.DeskConfigPush, new DeskConfigPushMessage(_store.Serialize(_config)))); }
            catch (Exception ex) { _log.LogDebug(ex, "Could not send the desk document to {Peers}", string.Join(", ", added)); }
        }
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    private async Task OnMessageReceived(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
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
                    state.Scenes, state.CurrentScene, IsController: false);
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
            if (DeskConfigStore.SameDesk(local, merged)) return;
            // The restart below reads this file back, so it has to be on disk first.
            await _store.SaveAsync(merged);
            _config = merged;
            _log.LogInformation("Desk settings updated by {Host}; restarting to apply them", sourceHost);
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
    private async Task SendToControllerAsync(byte[] payload)
    {
        var targets = _peers;
        if (targets.Length == 0) targets = await _world.GetMasters();
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

    private HydraConfigFile SafeLoad()
    {
        try { return _store.Load(); }
        catch (Exception) { return new HydraConfigFile(); }
    }
}
