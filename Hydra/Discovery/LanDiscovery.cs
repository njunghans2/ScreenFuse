using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hydra.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Discovery;

internal record LanDiscoveryBeacon(int Version, string Service, string Name, int Port, string Host,
    long IssuedAtUnixMs, string InstanceNonce, string Authenticator);

internal static class LanDiscoveryProtocol
{
    internal const int Port = 24802;
    internal static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.77.77");

    internal static byte[] Encode(string name, int servicePort, string host, string password,
        long? issuedAtUnixMs = null, string? instanceNonce = null)
    {
        var issued = issuedAtUnixMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nonce = instanceNonce ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        return JsonSerializer.SerializeToUtf8Bytes(new LanDiscoveryBeacon(1, "screenfuse", name, servicePort, host,
            issued, nonce, Convert.ToBase64String(Authenticate(name, servicePort, host, issued, nonce, password))));
    }

    internal static bool TryDecode(ReadOnlySpan<byte> bytes, string password, out LanDiscoveryBeacon? beacon)
    {
        beacon = null;
        try
        {
            if (bytes.Length is 0 or > 2048) return false;
            beacon = JsonSerializer.Deserialize<LanDiscoveryBeacon>(bytes);
            if (beacon is not { Version: 1, Service: "screenfuse", Port: >= 1024 and <= 65535 } ||
                string.IsNullOrWhiteSpace(beacon.Name) || beacon.Name.Length > 128 ||
                string.IsNullOrWhiteSpace(beacon.Host) || beacon.Host.Length > 128 ||
                Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - beacon.IssuedAtUnixMs) > 15_000 ||
                Convert.FromBase64String(beacon.InstanceNonce).Length != 16) return false;
            var supplied = Convert.FromBase64String(beacon.Authenticator);
            return supplied.Length == 32 && CryptographicOperations.FixedTimeEquals(supplied,
                Authenticate(beacon.Name, beacon.Port, beacon.Host, beacon.IssuedAtUnixMs, beacon.InstanceNonce, password));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            beacon = null;
            return false;
        }
    }

    private static byte[] Authenticate(string name, int port, string host, long issuedAtUnixMs,
        string instanceNonce, string password)
    {
        var canonical = Encoding.UTF8.GetBytes($"2\nscreenfuse\n{name.ToLowerInvariant()}\n{port}\n{host}\n{issuedAtUnixMs}\n{instanceNonce}");
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(password), canonical);
    }
}

internal static class LanDiscovery
{
    internal static async Task<string> FindServerAsync(string deskName, string password, CancellationToken cancellationToken = default)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false };
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, LanDiscoveryProtocol.Port));
        client.JoinMulticastGroup(LanDiscoveryProtocol.MulticastAddress);

        while (true)
        {
            var packet = await client.ReceiveAsync(cancellationToken);
            if (!LanDiscoveryProtocol.TryDecode(packet.Buffer, password, out var beacon) ||
                !beacon!.Name.Equals(deskName, StringComparison.OrdinalIgnoreCase))
                continue;

            return $"http://{packet.RemoteEndPoint.Address}:{beacon.Port}";
        }
    }
}

internal sealed class LanDiscoveryBroadcaster(
    EmbeddedStyxServerConfig config,
    IHydraProfile profile,
    ILogger<LanDiscoveryBroadcaster> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(config.DiscoveryName)) return;

        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.EnableBroadcast = true;
        client.MulticastLoopback = true;
        client.Ttl = 1;
        var instanceNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var multicast = new IPEndPoint(LanDiscoveryProtocol.MulticastAddress, LanDiscoveryProtocol.Port);
        var broadcast = new IPEndPoint(IPAddress.Broadcast, LanDiscoveryProtocol.Port);
        log.LogInformation("Advertising ScreenFuse desk {Desk} on the local network", config.DiscoveryName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var payload = LanDiscoveryProtocol.Encode(config.DiscoveryName, config.Port, profile.Name,
                    config.Password, instanceNonce: instanceNonce);
                await client.SendAsync(payload, multicast, stoppingToken);
                await client.SendAsync(payload, broadcast, stoppingToken);
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                log.LogDebug("LAN discovery broadcast failed: {Message}", ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
