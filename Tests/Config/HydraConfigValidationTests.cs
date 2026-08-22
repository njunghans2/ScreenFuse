using Hydra.Config;

namespace Tests.Config;

[TestFixture]
public class HydraConfigValidationTests
{
    [Test]
    public void AutoDiscovery_RequiresStrongSharedSecret()
    {
        var profiles = new List<HydraConfig>
        {
            new() { Mode = Mode.Slave, EmbeddedStyx = new EmbeddedStyxConfig { Server = "auto://studio", Password = "short" } }
        };
        Assert.That(() => HydraConfig.Validate(profiles, "client"),
            Throws.InvalidOperationException.With.Message.Contains("at least 16"));
    }
    private static string AsFile(string profilesJson) => $$"""{"profiles":{{profilesJson}}}""";

    // ─── relay requirement ────────────────────────────────────────────────────

    [Test]
    public void Validate_NoRelay_Throws()
    {
        var json = AsFile("""[{"mode":"Master"}]""");
        var ex = Assert.Throws<InvalidOperationException>(() => HydraConfig.ParseAndValidate(json));
        Assert.That(ex!.Message, Does.Contain("no relay configured"));
    }

    [Test]
    public void Validate_WithNetworkConfig_Passes()
    {
        var json = AsFile("""[{"mode":"Master","networkConfig":"embedded|http://localhost:5000|pw"}]""");
        Assert.DoesNotThrow(() => HydraConfig.ParseAndValidate(json));
    }

    [Test]
    public void Validate_WithEmbeddedStyx_Passes()
    {
        var json = AsFile("""[{"mode":"Master","embeddedStyx":{"server":"http://localhost:5000","password":"pw"}}]""");
        Assert.DoesNotThrow(() => HydraConfig.ParseAndValidate(json));
    }

    [Test]
    public void Validate_WithEmbeddedStyxServer_Passes()
    {
        var json = AsFile("""[{"mode":"Master","embeddedStyxServer":{"port":5000,"password":"pw"}}]""");
        Assert.DoesNotThrow(() => HydraConfig.ParseAndValidate(json));
    }

    [Test]
    public void Validate_MultipleProfiles_AllRequireRelay()
    {
        var json = AsFile("""
            [
              {"mode":"Master","embeddedStyx":{"server":"http://localhost:5000","password":"pw"},"conditions":{"ssid":"home"}},
              {"mode":"Master"}
            ]
            """);
        var ex = Assert.Throws<InvalidOperationException>(() => HydraConfig.ParseAndValidate(json));
        Assert.That(ex!.Message, Does.Contain("no relay configured"));
    }
}
