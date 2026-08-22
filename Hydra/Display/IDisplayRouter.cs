using Hydra.Config;

namespace Hydra.Display;

public record DisplayCommandResult(string Command, bool Success, string? Detail = null);

// One physical monitor this machine can currently reach over DDC/CI. A monitor showing another
// computer's input usually disappears from the local enumeration entirely, so an inventory is
// also the answer to "which monitors am I the active source for right now".
// CurrentInput is the live VCP 0x60 value — the code that selects *this* machine on that monitor.
// Aliases are every name this computer knows the monitor by. They matter because the same panel is
// named differently by each operating system — Windows calls one "Generic PnP Monitor" while the
// monitor's own capabilities string says "AORUS" and macOS says "AORUS FI27Q-X" — and recognising
// those three as one monitor is the whole job of the desk.
public record PhysicalMonitorInfo(
    string Id,
    string Description,
    string? LogicalName = null,
    int? CurrentInput = null,
    IReadOnlyList<int>? SupportedInputs = null,
    IReadOnlyList<string>? Aliases = null);

public interface IDisplayRouter
{
    Task<IReadOnlyList<DisplayCommandResult>> ApplyAsync(DisplayRoutingConfig routing, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DisplayCommandResult>> DoctorAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PhysicalMonitorInfo>> InventoryAsync(CancellationToken cancellationToken = default);
    Task<DisplayCommandResult> SetInputAsync(string id, int input, CancellationToken cancellationToken = default);
}
