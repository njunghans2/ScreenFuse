using Hydra.Discovery;

namespace Tests.Discovery;

public class LanDiscoveryProtocolTests
{
    [Test]
    public void Beacon_RoundTrips()
    {
        var bytes = LanDiscoveryProtocol.Encode("studio", 5000, "desktop", "correct horse battery staple");
        Assert.That(LanDiscoveryProtocol.TryDecode(bytes, "correct horse battery staple", out var beacon), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(beacon!.Name, Is.EqualTo("studio"));
            Assert.That(beacon.Port, Is.EqualTo(5000));
            Assert.That(beacon.Host, Is.EqualTo("desktop"));
        }
    }

    [Test]
    public void Beacon_RejectsMalformedPayload()
    {
        Assert.That(LanDiscoveryProtocol.TryDecode("not-json"u8, "secret", out _), Is.False);
    }

    [Test]
    public void Beacon_RejectsWrongDeskSecret()
    {
        var bytes = LanDiscoveryProtocol.Encode("studio", 5000, "desktop", "right-secret");
        Assert.That(LanDiscoveryProtocol.TryDecode(bytes, "wrong-secret", out _), Is.False);
    }

    [Test]
    public void Beacon_RejectsAuthenticatedButStaleReplay()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        var bytes = LanDiscoveryProtocol.Encode("studio", 5000, "desktop", "right-secret", stale);
        Assert.That(LanDiscoveryProtocol.TryDecode(bytes, "right-secret", out _), Is.False);
    }
}
