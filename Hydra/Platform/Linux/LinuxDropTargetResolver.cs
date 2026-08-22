using Hydra.FileTransfer;

namespace Hydra.Platform.Linux;

public sealed class LinuxDropTargetResolver : IDropTargetResolver
{
    public string? GetPasteDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in new[] { Path.Combine(home, "Downloads"), Path.Combine(home, "Desktop"), home })
            if (Directory.Exists(candidate)) return candidate;
        return null;
    }

    public void MoveToDestination(string tempDir, string destDir) => FileUtils.MoveTo(tempDir, destDir);
}
