namespace Hydra.Platform;

internal static class RunMode
{
    internal static bool IsSessionChild { get; set; }

    // The thread the process started on. Avalonia has to be initialised there — on macOS AppKit
    // refuses anything else — and a single await before the tray starts quietly moves us off it,
    // because a console app has no synchronisation context to come back on.
    internal static readonly int MainThreadId = Environment.CurrentManagedThreadId;

    internal static bool OnMainThread => Environment.CurrentManagedThreadId == MainThreadId;

    // Whether this process can put a window on a screen. Headless Linux has no display to put one
    // on; everywhere else the tray is running and Avalonia is up.
    internal static bool CanShowWindows =>
        !OperatingSystem.IsLinux() || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));
}
