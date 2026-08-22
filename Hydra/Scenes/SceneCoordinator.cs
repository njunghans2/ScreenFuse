using Hydra.Config;
using Hydra.Display;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Scenes;

public sealed class SceneCoordinator(
    List<HydraConfig> profiles,
    IHydraProfile activeProfile,
    IDisplayRouter displayRouter,
    IRelaySender relay,
    SceneOverrideStore store,
    ILogger<SceneCoordinator> log,
    Action? restart = null,
    TimeSpan? restartDelay = null) : ISceneCoordinator, IHostedService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string[] _peers = [];
    private int _restartScheduled;
    private readonly Action _restart = restart ?? ProcessRestart.Restart;
    private readonly TimeSpan _restartDelay = restartDelay ?? TimeSpan.FromMilliseconds(750);

    public string? CurrentScene => activeProfile.ProfileName;
    public IReadOnlyList<string> AvailableScenes { get; } = profiles
        .Where(p => !string.IsNullOrWhiteSpace(p.ProfileName))
        .Select(p => p.ProfileName!)
        .ToList();
    public IReadOnlyList<string> ConnectedPeers => _peers;
    public IReadOnlyList<string> ExpectedPeers { get; } = activeProfile.RemoteHosts
        .Select(h => h.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        relay.MessageReceived += OnMessageReceived;
        relay.PeersChanged += OnPeersChanged;
        var results = await displayRouter.ApplyAsync(activeProfile.DisplayRouting, cancellationToken);
        foreach (var result in results.Where(r => !r.Success))
            log.LogWarning("Startup display route failed for {Command}: {Detail}", result.Command, result.Detail);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        relay.MessageReceived -= OnMessageReceived;
        relay.PeersChanged -= OnPeersChanged;
        return Task.CompletedTask;
    }

    public async Task<SceneActivationResult> ActivateAsync(string scene, CancellationToken cancellationToken = default)
    {
        if (activeProfile.Mode != Mode.Master)
            return new(false, scene, "Scenes must be activated on the current master computer.");
        if (activeProfile.RemoteHosts.Any() && !relay.IsConnected)
            return new(false, scene, "No peers are connected; scene switching was not started to avoid a partial desk state.");
        var expectedPeers = activeProfile.RemoteHosts.Select(h => h.Name).Distinct(StringComparer.OrdinalIgnoreCase);
        var missingPeers = expectedPeers.Except(_peers, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missingPeers.Length > 0)
            return new(false, scene, $"Waiting for configured peers: {string.Join(", ", missingPeers)}. Scene switching was not started.");
        return await ActivateCoreAsync(scene, broadcast: true, cancellationToken);
    }

    private async Task<SceneActivationResult> ActivateCoreAsync(string scene, bool broadcast, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var target = profiles.FirstOrDefault(p => p.ProfileName?.Equals(scene, StringComparison.OrdinalIgnoreCase) == true);
            if (target == null)
                return new(false, scene, $"Unknown scene. Available: {string.Join(", ", AvailableScenes)}");
            if (CurrentScene?.Equals(target.ProfileName, StringComparison.OrdinalIgnoreCase) == true)
                return new(true, target.ProfileName!, "Scene is already active.");

            log.LogInformation("Activating scene {Scene}", target.ProfileName);
            var commands = await displayRouter.ApplyAsync(target.DisplayRouting, cancellationToken);

            if (broadcast && _peers.Length > 0)
            {
                var payload = MessageSerializer.Encode(MessageKind.SceneActivate, new SceneActivateMessage(target.ProfileName!));
                relay.Send(_peers, payload);
            }

            store.Write(target.ProfileName!);
            ScheduleRestart();
            return new(true, target.ProfileName!, "Display routing applied; agents are switching scenes.", commands);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task OnPeersChanged(string[] peers)
    {
        var added = peers.Except(_peers, StringComparer.OrdinalIgnoreCase).ToArray();
        _peers = peers;
        if (activeProfile.Mode == Mode.Master && added.Length > 0 && CurrentScene != null)
        {
            var payload = MessageSerializer.Encode(MessageKind.SceneActivate, new SceneActivateMessage(CurrentScene));
            relay.Send(added, payload);
        }
        return Task.CompletedTask;
    }

    private async Task OnMessageReceived(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        if (kind != MessageKind.SceneActivate) return;
        var message = new DecodedMessage(kind, body).Deserialize<SceneActivateMessage>();
        log.LogInformation("Scene {Scene} requested by {Host}", message.Scene, sourceHost);
        _ = await ActivateCoreAsync(message.Scene, broadcast: false, CancellationToken.None);
    }

    private void ScheduleRestart()
    {
        if (Interlocked.Exchange(ref _restartScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            // Let the relay flush the scene message and the HTTP API return a response first.
            await Task.Delay(_restartDelay);
            _restart();
        });
    }
}
