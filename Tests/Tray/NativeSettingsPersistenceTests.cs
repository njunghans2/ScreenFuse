using Hydra.Config;
using Hydra.Tray;

namespace Tests.Tray;

public class NativeSettingsPersistenceTests
{
    [Test]
    public void JoinCode_RoundTripsDeskSecretAndScenes()
    {
        var code = NativeJoinCode.Encode("Office: east", "a/secure+secret=with symbols", ["PC focus", "Mac | focus"]);
        var decoded = NativeJoinCode.Decode(code);

        Assert.Multiple(() =>
        {
            Assert.That(decoded.Desk, Is.EqualTo("Office: east"));
            Assert.That(decoded.Secret, Is.EqualTo("a/secure+secret=with symbols"));
            Assert.That(decoded.Scenes, Is.EqualTo(new[] { "PC focus", "Mac | focus" }));
        });
    }

    [Test]
    public async Task SaveAsync_WritesAValidatedConfigAndKeepsBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"screenfuse-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "screenfuse.conf");
        Directory.CreateDirectory(directory);
        try
        {
            var config = Config("desk-secret-which-is-long-enough");
            await NativeSettingsPersistence.SaveAsync(config, path);
            await NativeSettingsPersistence.SaveAsync(Config("replacement-secret-long-enough"), path);

            var loaded = HydraConfigFile.Parse(await File.ReadAllTextAsync(path), path);
            var backup = HydraConfigFile.Parse(await File.ReadAllTextAsync(path + ".bak"), path + ".bak");
            Assert.Multiple(() =>
            {
                Assert.That(loaded.Name, Is.EqualTo("main"));
                Assert.That(loaded.Profiles[0].EmbeddedStyxServer?.Password, Is.EqualTo("replacement-secret-long-enough"));
                Assert.That(backup.Profiles[0].EmbeddedStyxServer?.Password, Is.EqualTo("desk-secret-which-is-long-enough"));
            });
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static HydraConfigFile Config(string secret) => new()
    {
        Name = "main",
        Profiles =
        [
            new HydraConfig
            {
                ProfileName = "Default",
                Mode = Mode.Master,
                EmbeddedStyxServer = new EmbeddedStyxServerConfig { Port = 5000, Password = secret, DiscoveryName = "studio" },
                Hosts = [new HostConfig { Name = "main" }],
            },
        ],
    };
}
