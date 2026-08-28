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

    // Stops or resumes this computer's video output. A monitor with automatic input detection
    // follows the signal, which hands it over without any DDC involvement — the escape hatch for
    // computers with no DDC helper and monitors that ignore VCP 0x60. It is all of this computer's
    // displays or none, so it cannot hand over one monitor of several.
    //
    // force is the user asking outright, rather than a switch waking what it is about to need. A
    // wake during a switch reconnects a display macOS dropped only on a computer left with nothing
    // to render on, because taking back every released display would claim monitors this computer
    // had been handed off. Asked for by hand, taking them all back is exactly the request — and
    // without it the rescue is silently a no-op on every laptop, which always has its own panel.
    Task<DisplayCommandResult> SetDisplayPowerAsync(bool wake, bool force = false, CancellationToken cancellationToken = default);

    // Removes this computer's display for one monitor from (or restores it to) the desktop
    // topology, so a display that switched to another computer stops being part of this one's
    // desktop — windows and the pointer would otherwise keep living on its invisible area.
    // localSourceId is this computer's identity for the monitor: a GDI device name on Windows
    // ("\\.\DISPLAY1"), the display UUID on macOS.
    Task<DisplayCommandResult> SetMonitorDisplayEnabledAsync(string localSourceId, bool enabled, CancellationToken cancellationToken = default);

    // Blanks one panel (standby) or brings it back, without touching the desktop topology: the
    // display stays exactly where it is. Used for the last display of an OS, which must never be
    // removed — an OS with nothing to render is a soft-locked OS. On macOS the display-wide sleep
    // is used, since panels there have no per-monitor DDC.
    //
    // Only for panels this computer alone drives. The Windows path is DDC, which addresses the
    // monitor and not the cable, so a monitor another computer is showing goes black for them too —
    // "stop this computer's output" is SetMonitorDisplayEnabledAsync or SetDisplayPowerAsync.
    Task<DisplayCommandResult> SetDisplayStandbyAsync(string localSourceId, bool standby, CancellationToken cancellationToken = default);
}
