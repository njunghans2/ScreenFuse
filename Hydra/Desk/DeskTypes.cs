namespace Hydra.Desk;

// One physical monitor as the desk sees it: where it sits, which computer is on it right now, and
// which computers could be. Sources with no input code are panels that cannot be switched at all
// (a laptop display), so the settings window shows them as fixed rather than offering a dead choice.
public record DeskMonitorView(
    string Id,
    string Label,
    int DeskX,
    int DeskY,
    int Width,
    int Height,
    string? ActiveHost,
    IReadOnlyList<DeskSourceView> Sources,
    // Blanked as the last display of a computer: it still shows that computer, but black, and the
    // pointer may not enter it until a switch brings the desk back.
    bool Sleeping = false)
{
    // More than one computer is wired to it. Whether the switch can actually be carried out also
    // needs an input code for the target, which the desk learns or the user supplies once.
    public bool Switchable => Sources.Count > 1;
    public DeskSourceView? Source(string host) => Sources.FirstOrDefault(s => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase));
}

public record DeskSourceView(string Host, int? Input, bool Reachable, IReadOnlyList<int>? AvailableInputs = null);

public record DeskSnapshot(
    string Controller,
    string LocalHost,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<string> ConnectedHosts,
    IReadOnlyList<DeskMonitorView> Monitors,
    IReadOnlyList<string> Scenes,
    string? CurrentScene,
    bool IsController,
    // Where the pointer can leave one computer for another, as "NINOG Left Mac". Reported because
    // an empty list here is the difference between a desk that works and one that only looks right.
    IReadOnlyList<string>? Crossings = null,
    // False until the desk has finished a round. The placeholder used before then names this
    // machine as the one holding the keyboard, because it has to name someone -- and that guess
    // reads exactly like a fact in every diagnostic, which is worse than saying nothing. A desk
    // that has not run yet is not a desk with no monitors; it is a desk that has not run yet.
    bool Ready = true)
{
    public static DeskSnapshot Empty(string localHost) =>
        new(localHost, localHost, [localHost], [], [], [], null, true, [], Ready: false);
}

public record DeskActionResult(bool Accepted, string Message)
{
    public static DeskActionResult Ok(string message) => new(true, message);
    public static DeskActionResult Fail(string message) => new(false, message);
}

// Where the user dragged a monitor in the arrangement view.
public record DeskPlacement(string Monitor, int DeskX, int DeskY, int Width, int Height, string? Label = null);

public interface IDeskService
{
    DeskSnapshot Snapshot { get; }

    // Raised whenever the desk changes — new peer, monitor switched, scene activated. The settings
    // window redraws from Snapshot; it never polls.
    event Action? Changed;

    // Put a computer on a monitor. Takes effect immediately: no save, no restart. The DDC command is
    // executed by whichever peer currently drives that monitor, because a computer that is not the
    // active source usually cannot see the monitor at all.
    Task<DeskActionResult> SetMonitorHostAsync(string monitorId, string host, CancellationToken cancellationToken = default);

    // Hand the keyboard and mouse to another computer. Every agent restarts into the new roles.
    Task<DeskActionResult> SetControllerAsync(string host, CancellationToken cancellationToken = default);

    // Capture the desk exactly as it stands — every monitor's computer plus who has control — as a
    // named profile, and share it with the peers.
    Task<DeskActionResult> SaveSceneAsync(string name, CancellationToken cancellationToken = default);
    Task<DeskActionResult> DeleteSceneAsync(string name, CancellationToken cancellationToken = default);
    Task<DeskActionResult> ActivateSceneAsync(string name, CancellationToken cancellationToken = default);

    // Troubleshooting: wake the displays on this computer and on every connected peer, so monitors
    // that drifted to sleep or lost their signal can re-lock onto the right computer.
    Task<DeskActionResult> WakeAllDisplaysAsync(CancellationToken cancellationToken = default);

    // Troubleshooting: ask every connected peer to restore its cursor (a stranded pointer leaves a
    // machine with a hidden cursor and no way back).
    Task<DeskActionResult> ResetCursorsAsync(CancellationToken cancellationToken = default);

    // Persist the arrangement the user dragged, and rebuild the crossing edges from it.
    Task<DeskActionResult> SaveArrangementAsync(IReadOnlyList<DeskPlacement> placements, CancellationToken cancellationToken = default);
}
