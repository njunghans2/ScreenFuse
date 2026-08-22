using System.Diagnostics;
using Hydra.FileTransfer;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

// Linux file managers expose copied files as text/uri-list (and GNOME's copied-files
// target) on the clipboard. This works across Nautilus, Dolphin, Nemo, Caja and Thunar
// without desktop-specific accessibility APIs: select files, press Ctrl+C, then use the
// ScreenFuse copy/paste hotkeys.
public sealed class LinuxFileSelectionDetector(ILogger<LinuxFileSelectionDetector> log) : IFileSelectionDetector
{
    public string FileManagerName => "Linux file manager";
    public bool IsFileTransferSupported => FindClipboardTool() != null;

    public FileSelectionResult GetSelectedPaths()
    {
        foreach (var mime in new[] { "text/uri-list", "x-special/gnome-copied-files" })
        {
            var text = ReadClipboard(mime);
            if (string.IsNullOrWhiteSpace(text)) continue;
            var paths = ParseUriList(text);
            if (paths.Count > 0) return new(true, paths);
        }
        return new(true, null);
    }

    internal static List<string> ParseUriList(string text)
    {
        var result = new List<string>();
        foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line is "copy" or "cut") continue;
            var path = FileUtils.FileUrlToLocalPath(line);
            if (path != null && (File.Exists(path) || Directory.Exists(path))) result.Add(path);
        }
        return result;
    }

    private string? ReadClipboard(string mime)
    {
        var tool = FindClipboardTool();
        if (tool == null) return null;
        try
        {
            var psi = new ProcessStartInfo(tool.Value.FileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in tool.Value.Arguments(mime)) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(1500) || process.ExitCode != 0) return null;
            return output;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not read Linux file clipboard");
            return null;
        }
    }

    private static (string FileName, Func<string, string[]> Arguments)? FindClipboardTool()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) && CommandExists("wl-paste"))
            return ("wl-paste", mime => ["--no-newline", "--type", mime]);
        if (CommandExists("xclip"))
            return ("xclip", mime => ["-selection", "clipboard", "-t", mime, "-o"]);
        return null;
    }

    private static bool CommandExists(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator).Any(dir => File.Exists(Path.Combine(dir, name)));
    }
}
