using Hydra.Config;

namespace Tests.Display;

public class DisplayRoutingConfigTests
{
    private const string Relay = "\"embeddedStyx\":{\"server\":\"http://localhost:5000\",\"password\":\"pw\"}";

    [Test]
    public void RejectsOutOfRangeInput()
    {
        var json = $$$"""
            {"profiles":[{"mode":"Master",{{{Relay}}},"displayRouting":{"inputs":[{"id":"*","input":256}]}}]}
            """;
        var ex = Assert.Throws<InvalidOperationException>(() => HydraConfigFile.Parse(json, "test.conf"));
        Assert.That(ex!.Message, Does.Contain("between 0 and 255"));
    }

    [Test]
    public void RejectsSimultaneousWakeAndSleep()
    {
        var json = $$$"""
            {"profiles":[{"mode":"Master",{{{Relay}}},"displayRouting":{"wakeDisplays":true,"sleepDisplays":true}}]}
            """;
        var ex = Assert.Throws<InvalidOperationException>(() => HydraConfigFile.Parse(json, "test.conf"));
        Assert.That(ex!.Message, Does.Contain("cannot wake and sleep"));
    }
}
