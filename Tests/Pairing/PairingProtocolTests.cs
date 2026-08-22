using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using Hydra.Config;
using Hydra.Pairing;
using Hydra.Screen;

namespace Tests.Pairing;

public class PairingProtocolTests
{
    [Test]
    public async Task TwoFreshInstallations_DiscoverAndMutuallyApproveOnTheLanSocket()
    {
        var firstPort = FreeUdpPort();
        var secondPort = FreeUdpPort();
        while (secondPort == firstPort) secondPort = FreeUdpPort();
        await using var first = new PairingDiscovery(firstPort, secondPort, IPAddress.Loopback);
        await using var second = new PairingDiscovery(secondPort, firstPort, IPAddress.Loopback);
        var firstFound = Source<PairingCandidate>();
        var secondFound = Source<PairingCandidate>();
        var firstCompleted = Source<PairingCandidate>();
        var secondCompleted = Source<PairingCandidate>();
        first.CandidateFound += candidate => firstFound.TrySetResult(candidate);
        second.CandidateFound += candidate => secondFound.TrySetResult(candidate);
        first.PairingCompleted += candidate => firstCompleted.TrySetResult(candidate);
        second.PairingCompleted += candidate => secondCompleted.TrySetResult(candidate);

        await first.StartAsync();
        await second.StartAsync();
        var candidates = await Task.WhenAll(firstFound.Task, secondFound.Task).WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Multiple(() =>
        {
            Assert.That(candidates[0].RelaySecret, Is.EqualTo(candidates[1].RelaySecret));
            Assert.That(candidates[0].VerificationCode, Is.EqualTo(candidates[1].VerificationCode));
            Assert.That(candidates[0].LocalIsMaster, Is.Not.EqualTo(candidates[1].LocalIsMaster));
            Assert.That(firstCompleted.Task.IsCompleted, Is.False, "discovery alone must not complete pairing");
        });

        await first.ApproveAsync(candidates[0]);
        Assert.That(firstCompleted.Task.IsCompleted, Is.False, "one-sided approval must not complete pairing");
        await second.ApproveAsync(candidates[1]);
        var completed = await Task.WhenAll(firstCompleted.Task, secondCompleted.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(completed.Select(c => c.RelaySecret).Distinct().Count(), Is.EqualTo(1));
    }

    [Test]
    public void BothComputers_DeriveSameSecretCodeDeskAndOppositeFrozenRoles()
    {
        using var aKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var aId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var bId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var aPublic = aKey.ExportSubjectPublicKeyInfo();
        var bPublic = bKey.ExportSubjectPublicKeyInfo();
        var aNonce = RandomNumberGenerator.GetBytes(32);
        var bNonce = RandomNumberGenerator.GetBytes(32);
        var aBeacon = Reveal(aId, "alpha", 1000, aPublic, aNonce, bId);
        var bBeacon = Reveal(bId, "beta", 2000, bPublic, bNonce, aId);

        var a = PairingProtocol.Derive(aId, "alpha", 1000, aPublic, aNonce, aKey, bBeacon);
        var b = PairingProtocol.Derive(bId, "beta", 2000, bPublic, bNonce, bKey, aBeacon);

        Assert.Multiple(() =>
        {
            Assert.That(a.RelaySecret, Is.EqualTo(b.RelaySecret));
            Assert.That(a.VerificationCode, Is.EqualTo(b.VerificationCode));
            Assert.That(a.DeskName, Is.EqualTo(b.DeskName));
            Assert.That(a.LocalIsMaster, Is.True);
            Assert.That(b.LocalIsMaster, Is.False);
        });
    }

    [Test]
    public void PairingBeacon_RequiresValidCommitmentBeforeUse()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var id = Guid.NewGuid();
        var target = Guid.NewGuid();
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var nonce = RandomNumberGenerator.GetBytes(32);
        var commitment = PairingProtocol.CreateCommitment(id, "desktop", 1234, publicKey, nonce);
        var commit = new PairingBeacon(1, PairingProtocol.BeaconService, id, "desktop", 1234, commitment);
        var reveal = commit with { TargetId = target, PublicKey = Convert.ToBase64String(publicKey), Nonce = Convert.ToBase64String(nonce) };
        var tampered = reveal with { Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };

        Assert.Multiple(() =>
        {
            Assert.That(PairingProtocol.TryDecode(PairingProtocol.Encode(commit), out var decoded), Is.True);
            Assert.That(decoded?.Host, Is.EqualTo("desktop"));
            Assert.That(PairingProtocol.TryDecode(PairingProtocol.Encode(reveal), out _), Is.True);
            Assert.That(PairingProtocol.TryDecode(PairingProtocol.Encode(tampered), out _), Is.False);
            Assert.That(PairingProtocol.TryDecode("not json"u8, out _), Is.False);
            Assert.That(PairingProtocol.TryDecode(new byte[PairingProtocol.MaxPacketBytes + 1], out _), Is.False);
        });
    }

    [Test]
    public void PairedConfigs_UseDetectedDisplayGeometryWhenItProvidesAnEdge()
    {
        var local = new PairingDesktopLayout([new PairingScreenBounds(0, 0, 1920, 1080)]);
        var remote = new PairingDesktopLayout([new PairingScreenBounds(0, 0, 1920, 1080), new PairingScreenBounds(1920, 0, 1920, 1080)]);
        var config = PairedDeskConfig.Create("alpha", "beta", true, "desk", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), local, remote);

        Assert.That(config.Profiles[0].Hosts[0].Neighbours[0].Direction, Is.EqualTo(Direction.Right));
    }

    [Test]
    public void Approval_IsBoundToBothComputersAndTheDerivedSecret()
    {
        var localId = Guid.NewGuid();
        var remoteId = Guid.NewGuid();
        var candidateAtRemote = new PairingCandidate(localId, "alpha", "beta", "alpha", "123456", false,
            "desk-test", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var candidateAtLocal = candidateAtRemote with { InstanceId = remoteId, LocalConfigName = "alpha", RemoteConfigName = "beta" };
        var approval = PairingProtocol.CreateApproval(localId, candidateAtLocal);

        Assert.Multiple(() =>
        {
            Assert.That(PairingProtocol.VerifyApproval(remoteId, candidateAtRemote, approval), Is.True);
            Assert.That(PairingProtocol.VerifyApproval(Guid.NewGuid(), candidateAtRemote, approval), Is.False);
            Assert.That(PairingProtocol.TryDecodeApproval(PairingProtocol.Encode(approval), out _), Is.True);
        });
    }

    [Test]
    public void PairedConfigs_AreValidAndNeedNoManualNetworkFields()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var master = PairedDeskConfig.Create("alpha", "beta", true, "desk-abcd", secret, Direction.Left);
        var slave = PairedDeskConfig.Create("beta", "alpha", false, "desk-abcd", secret);

        var parsedMaster = HydraConfigFile.Parse(NativeJson(master), "master.conf");
        var parsedSlave = HydraConfigFile.Parse(NativeJson(slave), "slave.conf");

        Assert.Multiple(() =>
        {
            Assert.That(parsedMaster.Profiles[0].EmbeddedStyxServer?.DiscoveryName, Is.EqualTo("desk-abcd"));
            Assert.That(parsedMaster.Profiles[0].Hosts[0].Neighbours[0].Direction, Is.EqualTo(Direction.Left));
            Assert.That(parsedSlave.Profiles[0].EmbeddedStyx?.Server, Is.EqualTo("auto://desk-abcd"));
            Assert.That(parsedSlave.Profiles[0].Mode, Is.EqualTo(Mode.Slave));
            Assert.That(parsedMaster.LockFile, Is.EqualTo(".screenfuse.lock"));
        });
    }

    private static PairingBeacon Reveal(Guid id, string host, long startedAt, byte[] publicKey, byte[] nonce, Guid target) =>
        new(1, PairingProtocol.BeaconService, id, host, startedAt,
            PairingProtocol.CreateCommitment(id, host, startedAt, publicKey, nonce), target,
            Convert.ToBase64String(publicKey), Convert.ToBase64String(nonce));

    private static TaskCompletionSource<T> Source<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static string NativeJson(HydraConfigFile config) =>
        Hydra.Tray.NativeSettingsPersistence.SerializeAndValidate(config, "pair.conf");
}
