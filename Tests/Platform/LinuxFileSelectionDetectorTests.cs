using Hydra.Platform.Linux;

namespace Tests.Platform;

public class LinuxFileSelectionDetectorTests
{
    [Test]
    public void ParseUriList_HandlesGnomeHeaderCommentsAndEscapes()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"screenfuse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "hello world.txt");
            File.WriteAllText(file, "test");
            var uri = new Uri(file).AbsoluteUri;
            var result = LinuxFileSelectionDetector.ParseUriList($"copy\n# comment\n{uri}\n");
            Assert.That(result, Is.EqualTo(new[] { file }));
        }
        finally { Directory.Delete(dir, true); }
    }
}
