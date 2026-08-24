using System.Diagnostics;
using System.Runtime.InteropServices;
using Cathedral.Utils;

namespace Hydra.Platform;

internal static partial class ProcessRestart
{
    private static readonly Toggle Restarting = new(); // one-shot latch — restart already initiated
    private static volatile bool _quitting;

    // Quit has to win against every other reason to come back.
    //
    // Several subsystems restart ScreenFuse on their own — a scene activating, the desk changing
    // hands, the network moving, an update installing — and they do it after a short delay. Quit
    // inside that window and the delayed restart still fires; on macOS it is an execv, so the
    // process comes back with the same PID and it looks for all the world like quitting did
    // nothing. Once the user has said quit, nothing restarts.
    internal static void PreventRestarts()
    {
        _quitting = true;
        Restarting.TrySet(); // also latch out anything already past the check
    }

    internal static bool IsQuitting => _quitting;

    internal static void Restart() => Restart(null);

    // The reason is written to the agent log before anything else happens. Under launchd's KeepAlive
    // a crash and a deliberate restart look identical from the outside, so the absence of this line
    // in the log is what tells you ScreenFuse died rather than chose to come back.
    internal static void Restart(string? reason)
    {
        if (_quitting) return;

        // one restart only — a racing caller (NetworkWatcher + SelfUpdater, or an event burst) must not
        // spawn a second process (Windows) before Environment.Exit runs
        if (!Restarting.TrySet()) return;

        try { Console.Error.WriteLine($"[screenfuse] restarting: {reason ?? "unspecified"}"); }
        catch (IOException) { /* no console attached */ }

        // A restart while the cursor is hidden (mid-crossing) would leave the OS cursor blank:
        // the cursor is replaced or hidden by this process, and the new image starts believing
        // it is visible. Put it back before the exit, or the user gets an invisible cursor until
        // they reboot.
        try
        {
            if (OperatingSystem.IsWindows()) Windows.WindowsCursorSnapshot.RestoreDefaults();
            if (OperatingSystem.IsMacOS()) MacOs.NativeMethods.CGDisplayShowCursor(MacOs.NativeMethods.CGMainDisplayID());
        }
        catch (Exception) { /* best effort — the new process restores at startup too */ }

        var exePath = Environment.ProcessPath!;

        if (OperatingSystem.IsWindows())
        {
            // A service-owned session child must not spawn its own replacement; the watchdog
            // observes this exit and launches exactly one child in the active desktop session.
            if (RunMode.IsSessionChild)
                Environment.Exit(0);

            // windows has no exec() — start a new process and exit
            var info = new ProcessStartInfo { FileName = exePath, UseShellExecute = false };
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
                info.ArgumentList.Add(arg);
            try
            {
                Process.Start(info);
            }
            catch
            {
                Restarting.TryReset(); // spawn failed — don't latch out a later retry
                throw;
            }
            Environment.Exit(0);
        }
        else
        {
            // exec() replaces the process image in-place — same PID, same process group, terminal grip preserved
            var args = Environment.GetCommandLineArgs();
            var argv = new string?[args.Length + 1]; // null-terminated
            Array.Copy(args, argv, args.Length);
            _ = Execv(exePath, argv);
            Environment.Exit(1); // Execv only returns on failure
        }
    }

    [LibraryImport("libc", EntryPoint = "execv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Execv(string pathname, string?[] argv);
}
