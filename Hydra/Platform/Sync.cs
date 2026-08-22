namespace Hydra.Platform;

// Waits without leaving the thread.
//
// Everything before the tray starts has to stay on the process's main thread. An await hands the
// continuation to the thread pool — a console app has no synchronisation context to return on — and
// the first thing the tray does is initialise Avalonia, which on macOS must happen on the main
// thread. Awaiting during startup crashed ScreenFuse on every launch with "IDispatcherImpl belongs
// to a different thread"; launchd restarted it each time, so the Mac sat in a crash loop, invisible
// in Activity Monitor and impossible to quit.
//
// Blocking is safe here in a way it would not be elsewhere: this is startup, on a thread nothing
// else wants, with no context to deadlock against.
internal static class Sync
{
    internal static void Wait(Task task) => task.GetAwaiter().GetResult();
    internal static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();
    internal static T Wait<T>(ValueTask<T> task) => task.GetAwaiter().GetResult();
}
