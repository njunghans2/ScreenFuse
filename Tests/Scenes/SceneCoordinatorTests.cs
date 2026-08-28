using Hydra.Config;
using Hydra.Display;
using Hydra.Relay;
using Hydra.Scenes;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Scenes;

public class SceneCoordinatorTests
{
    [Test]
    public async Task Master_AppliesBroadcastsPersistsAndRestarts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"screenfuse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var target = new HydraConfig
            {
                Mode = Mode.Master,
                ProfileName = "Mac focus",
                DisplayRouting = new DisplayRoutingConfig { Inputs = [new MonitorInputConfig { Id = "DELL", Input = 17 }] },
            };
            var active = new HydraConfig
            {
                Mode = Mode.Master,
                ProfileName = "PC focus",
                Hosts = [new HostConfig { Name = "master" }, new HostConfig { Name = "peer" }],
            };
            var profile = new HydraProfile(new HydraConfigFile { Name = "master" }, active);
            var relay = new FakeRelay();
            var router = new FakeDisplayRouter();
            var store = new SceneOverrideStore(Path.Combine(dir, "screenfuse.conf"));
            var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var coordinator = new SceneCoordinator([active, target], profile, router, relay, store,
                NullLogger<SceneCoordinator>.Instance, desk: null, () => restarted.SetResult(), TimeSpan.Zero);
            await coordinator.StartAsync(CancellationToken.None);
            await relay.FirePeersChanged("peer");

            var result = await coordinator.ActivateAsync("Mac focus");
            await restarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Accepted, Is.True);
                Assert.That(router.Applied, Is.SameAs(target.DisplayRouting));
                Assert.That(relay.Sent, Has.Count.EqualTo(2));
                Assert.That(relay.Sent.Last().Kind, Is.EqualTo(MessageKind.SceneActivate));
                Assert.That(File.ReadAllText(store.Path).Trim(), Is.EqualTo("Mac focus"));
            }
            await coordinator.StopAsync(CancellationToken.None);
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class FakeDisplayRouter : IDisplayRouter
    {
        public DisplayRoutingConfig? Applied { get; private set; }
        public Task<IReadOnlyList<DisplayCommandResult>> ApplyAsync(DisplayRoutingConfig routing, CancellationToken cancellationToken = default)
        {
            Applied = routing;
            return Task.FromResult<IReadOnlyList<DisplayCommandResult>>([]);
        }
        public Task<IReadOnlyList<DisplayCommandResult>> DoctorAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DisplayCommandResult>>([]);
        public Task<IReadOnlyList<PhysicalMonitorInfo>> InventoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PhysicalMonitorInfo>>([]);
        public Task<DisplayCommandResult> SetInputAsync(string id, int input, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DisplayCommandResult($"set {id} input {input}", true));
        public Task<DisplayCommandResult> SetDisplayPowerAsync(bool wake, bool force = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DisplayCommandResult(wake ? "wake displays" : "sleep displays", true));
        public Task<DisplayCommandResult> SetMonitorDisplayEnabledAsync(string localSourceId, bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DisplayCommandResult(enabled ? "enable display" : "disable display", true));
        public Task<DisplayCommandResult> SetDisplayStandbyAsync(string localSourceId, bool standby, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DisplayCommandResult(standby ? "blank display" : "wake display", true));
    }
}
