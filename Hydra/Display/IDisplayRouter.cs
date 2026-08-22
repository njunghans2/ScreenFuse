using Hydra.Config;

namespace Hydra.Display;

public record DisplayCommandResult(string Command, bool Success, string? Detail = null);

// One physical monitor this machine can currently reach over DDC/CI. A monitor showing another
// computer's input usually disappears from the local enumeration entirely, so an inventory is
// also the answer to "which monitors am I the active source for right now".
// CurrentInput is the live VCP 0x60 value — the code that selects *this* machine on that monitor.
public record PhysicalMonitorInfo(string Id, string Description, string? LogicalName = null, int? CurrentInput = null);

public interface IDisplayRouter
{
    Task<IReadOnlyList<DisplayCommandResult>> ApplyAsync(DisplayRoutingConfig routing, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DisplayCommandResult>> DoctorAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PhysicalMonitorInfo>> InventoryAsync(CancellationToken cancellationToken = default);
    Task<DisplayCommandResult> SetInputAsync(string id, int input, CancellationToken cancellationToken = default);
}
