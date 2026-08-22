namespace Hydra.Scenes;

public record SceneActivationResult(bool Accepted, string Scene, string Message, IReadOnlyList<Display.DisplayCommandResult>? DisplayCommands = null);

public interface ISceneCoordinator
{
    string? CurrentScene { get; }
    IReadOnlyList<string> AvailableScenes { get; }
    IReadOnlyList<string> ConnectedPeers { get; }
    IReadOnlyList<string> ExpectedPeers { get; }
    Task<SceneActivationResult> ActivateAsync(string scene, CancellationToken cancellationToken = default);
}
