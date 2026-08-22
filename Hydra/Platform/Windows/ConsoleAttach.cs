using System.Runtime.InteropServices;

namespace Hydra.Platform.Windows;

// ScreenFuse is built as a GUI binary so the tray agent never opens a console window. That also
// detaches its standard output, which would silently swallow the command-line entry points, so the
// CLI paths re-attach to the console that launched them and rebind the streams.
internal static partial class ConsoleAttach
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    internal static void ToParent()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (!AttachConsole(AttachParentProcess)) return;
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
        catch (Exception)
        {
            // No parent console (started from Explorer or a service): writing nowhere is correct here.
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);
}
