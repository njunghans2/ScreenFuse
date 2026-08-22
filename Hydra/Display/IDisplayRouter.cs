using Hydra.Config;

namespace Hydra.Display;

public record DisplayCommandResult(string Command, bool Success, string? Detail = null);

public interface IDisplayRouter
{
    Task<IReadOnlyList<DisplayCommandResult>> ApplyAsync(DisplayRoutingConfig routing, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DisplayCommandResult>> DoctorAsync(CancellationToken cancellationToken = default);
}
