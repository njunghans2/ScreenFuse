using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hydra.Pairing;

internal record PairingBeacon(int Version, string Service, Guid InstanceId, string Host,
    long StartedAtUnixMs, string Commitment, Guid? TargetId = null, string? PublicKey = null, string? Nonce = null);

internal record PairingApproval(int Version, string Service, Guid FromId, Guid ToId, string Authenticator);

internal record PairingCandidate(Guid InstanceId, string Host, string LocalConfigName, string RemoteConfigName,
    string VerificationCode, bool LocalIsMaster, string DeskName, string RelaySecret);

internal static class PairingProtocol
{
    internal const int Port = 24803;
    internal const int MaxPacketBytes = 4096;
    internal const string BeaconService = "screenfuse-pair";
    internal const string ApprovalService = "screenfuse-pair-approval";
    internal static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.77.78");

    internal static byte[] Encode(PairingBeacon beacon) => JsonSerializer.SerializeToUtf8Bytes(beacon);
    internal static byte[] Encode(PairingApproval approval) => JsonSerializer.SerializeToUtf8Bytes(approval);

    internal static string CreateCommitment(Guid instanceId, string host, long startedAtUnixMs,
        ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> nonce) =>
        Convert.ToBase64String(HashParts("screenfuse-pair-commit-v1"u8.ToArray(), instanceId.ToByteArray(),
            Encoding.UTF8.GetBytes(host), Int64Bytes(startedAtUnixMs), publicKey.ToArray(), nonce.ToArray()));

    internal static bool TryDecode(ReadOnlySpan<byte> bytes, out PairingBeacon? beacon)
    {
        beacon = null;
        if (bytes.Length is 0 or > MaxPacketBytes) return false;
        try
        {
            beacon = JsonSerializer.Deserialize<PairingBeacon>(bytes);
            if (beacon is not { Version: 1, Service: BeaconService } || beacon.InstanceId == Guid.Empty
                || string.IsNullOrWhiteSpace(beacon.Host) || beacon.Host.Length > 128
                || beacon.StartedAtUnixMs <= 0 || !IsBase64Length(beacon.Commitment, 32)) return false;

            var isCommit = beacon.TargetId == null && beacon.PublicKey == null && beacon.Nonce == null;
            var isReveal = beacon.TargetId is { } target && target != Guid.Empty
                && beacon.PublicKey != null && beacon.Nonce != null;
            if (isCommit) return true;
            if (!isReveal || !IsBase64Length(beacon.Nonce!, 32)) return false;

            var publicKey = Convert.FromBase64String(beacon.PublicKey!);
            using var key = ECDiffieHellman.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            if (consumed != publicKey.Length) return false;
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(beacon.Commitment),
                Convert.FromBase64String(CreateCommitment(beacon.InstanceId, beacon.Host, beacon.StartedAtUnixMs,
                    publicKey, Convert.FromBase64String(beacon.Nonce!))));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException)
        {
            beacon = null;
            return false;
        }
    }

    internal static bool TryDecodeApproval(ReadOnlySpan<byte> bytes, out PairingApproval? approval)
    {
        approval = null;
        if (bytes.Length is 0 or > MaxPacketBytes) return false;
        try
        {
            approval = JsonSerializer.Deserialize<PairingApproval>(bytes);
            return approval is { Version: 1, Service: ApprovalService } && approval.FromId != Guid.Empty
                && approval.ToId != Guid.Empty && IsBase64Length(approval.Authenticator, 32);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            approval = null;
            return false;
        }
    }

    internal static PairingCandidate Derive(Guid localId, string localHost, long localStartedAtUnixMs,
        byte[] localPublicKey, byte[] localNonce, ECDiffieHellman localKey, PairingBeacon remote)
    {
        if (remote.PublicKey == null || remote.Nonce == null)
            throw new CryptographicException("The peer has not revealed its pairing key.");
        var remotePublic = Convert.FromBase64String(remote.PublicKey);
        var remoteNonce = Convert.FromBase64String(remote.Nonce);
        using var remoteKey = ECDiffieHellman.Create();
        remoteKey.ImportSubjectPublicKeyInfo(remotePublic, out _);
        var shared = localKey.DeriveKeyMaterial(remoteKey.PublicKey);

        var localFirst = localId.CompareTo(remote.InstanceId) < 0;
        var transcriptHash = localFirst
            ? HashTranscript(localId, localHost, localStartedAtUnixMs, localPublicKey, localNonce,
                remote.InstanceId, remote.Host, remote.StartedAtUnixMs, remotePublic, remoteNonce)
            : HashTranscript(remote.InstanceId, remote.Host, remote.StartedAtUnixMs, remotePublic, remoteNonce,
                localId, localHost, localStartedAtUnixMs, localPublicKey, localNonce);
        var relaySecret = HMACSHA256.HashData(shared, Combine(transcriptHash, "relay-root"u8));
        var verification = HMACSHA256.HashData(shared, Combine(transcriptHash, "sas"u8));
        var code = BinaryPrimitives.ReadUInt32BigEndian(verification) % 1_000_000;

        var localIsMaster = localStartedAtUnixMs != remote.StartedAtUnixMs
            ? localStartedAtUnixMs < remote.StartedAtUnixMs : localFirst;
        var deskHash = SHA256.HashData(Combine(transcriptHash, "desk"u8));
        var deskName = $"desk-{Convert.ToHexString(deskHash.AsSpan(0, 4)).ToLowerInvariant()}";
        var sameName = localHost.Equals(remote.Host, StringComparison.OrdinalIgnoreCase);
        var localConfigName = sameName ? $"{localHost}-{(localFirst ? "1" : "2")}" : localHost;
        var remoteConfigName = sameName ? $"{remote.Host}-{(localFirst ? "2" : "1")}" : remote.Host;
        return new PairingCandidate(remote.InstanceId, remote.Host, localConfigName, remoteConfigName,
            code.ToString("D6"), localIsMaster, deskName, Convert.ToBase64String(relaySecret));
    }

    internal static PairingApproval CreateApproval(Guid localId, PairingCandidate candidate)
    {
        var authenticator = HMACSHA256.HashData(Convert.FromBase64String(candidate.RelaySecret),
            ApprovalMessage(localId, candidate.InstanceId));
        return new PairingApproval(1, ApprovalService, localId, candidate.InstanceId,
            Convert.ToBase64String(authenticator));
    }

    internal static bool VerifyApproval(Guid localId, PairingCandidate candidate, PairingApproval approval)
    {
        if (approval.FromId != candidate.InstanceId || approval.ToId != localId) return false;
        var expected = HMACSHA256.HashData(Convert.FromBase64String(candidate.RelaySecret),
            ApprovalMessage(approval.FromId, approval.ToId));
        return CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(approval.Authenticator));
    }

    private static byte[] ApprovalMessage(Guid from, Guid to) => HashParts(
        "screenfuse-pair-approve-v1"u8.ToArray(), from.ToByteArray(), to.ToByteArray());

    private static byte[] HashTranscript(Guid firstId, string firstHost, long firstStarted, byte[] firstKey,
        byte[] firstNonce, Guid secondId, string secondHost, long secondStarted, byte[] secondKey, byte[] secondNonce) =>
        HashParts("screenfuse-pair-transcript-v1"u8.ToArray(), firstId.ToByteArray(), Encoding.UTF8.GetBytes(firstHost),
            Int64Bytes(firstStarted), firstKey, firstNonce, secondId.ToByteArray(), Encoding.UTF8.GetBytes(secondHost),
            Int64Bytes(secondStarted), secondKey, secondNonce);

    private static byte[] Int64Bytes(long value)
    {
        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }

    private static bool IsBase64Length(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024) return false;
        try { return Convert.FromBase64String(value).Length == length; }
        catch (FormatException) { return false; }
    }

    private static byte[] HashParts(params byte[][] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var part in parts)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, part.Length);
            hash.AppendData(length);
            hash.AppendData(part);
        }
        return hash.GetHashAndReset();
    }

    private static byte[] Combine(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var result = new byte[left.Length + right.Length];
        left.CopyTo(result);
        right.CopyTo(result.AsSpan(left.Length));
        return result;
    }
}

internal sealed class PairingDiscovery : IAsyncDisposable
{
    private readonly Guid _instanceId = Guid.NewGuid();
    private readonly string _host = Environment.MachineName.Split('.')[0];
    private readonly long _startedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _publicKey;
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(32);
    private readonly string _commitment;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly HashSet<Guid> _ignored = [];
    private UdpClient? _client;
    private Task? _sendLoop;
    private Task? _receiveLoop;
    private PairingBeacon? _remoteCommit;
    private PairingCandidate? _candidate;
    private bool _localApproved;
    private bool _peerApproved;
    private int _completionRaised;
    private int _disposed;

    internal event Action<PairingCandidate>? CandidateFound;
    internal event Action<PairingCandidate>? PairingCompleted;

    internal PairingDiscovery()
    {
        _publicKey = _key.ExportSubjectPublicKeyInfo();
        _commitment = PairingProtocol.CreateCommitment(_instanceId, _host, _startedAtUnixMs, _publicKey, _nonce);
    }

    internal Task StartAsync()
    {
        if (_client != null) return Task.CompletedTask;
        _client = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false };
        _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _client.Client.Bind(new IPEndPoint(IPAddress.Any, PairingProtocol.Port));
        _client.JoinMulticastGroup(PairingProtocol.MulticastAddress);
        _client.EnableBroadcast = true;
        _client.MulticastLoopback = true;
        _client.Ttl = 1;
        _sendLoop = SendLoopAsync(_stop.Token);
        _receiveLoop = ReceiveLoopAsync(_stop.Token);
        return Task.CompletedTask;
    }

    internal void IgnoreCandidate(Guid instanceId)
    {
        lock (_gate)
        {
            _ignored.Add(instanceId);
            if (_remoteCommit?.InstanceId != instanceId) return;
            _remoteCommit = null;
            _candidate = null;
            _localApproved = false;
            _peerApproved = false;
            _completionRaised = 0;
        }
    }

    internal async Task ApproveAsync(PairingCandidate candidate, CancellationToken cancellationToken = default)
    {
        PairingApproval approval;
        lock (_gate)
        {
            if (_candidate == null || _candidate.InstanceId != candidate.InstanceId)
                throw new InvalidOperationException("That computer is no longer available for pairing.");
            approval = PairingProtocol.CreateApproval(_instanceId, _candidate);
        }
        await SendPacketAsync(PairingProtocol.Encode(approval), cancellationToken);
        await Task.Delay(80, cancellationToken);
        await SendPacketAsync(PairingProtocol.Encode(approval), cancellationToken);

        PairingCandidate? completed = null;
        lock (_gate)
        {
            _localApproved = true;
            if (_peerApproved && Interlocked.Exchange(ref _completionRaised, 1) == 0) completed = _candidate;
        }
        if (completed != null) PairingCompleted?.Invoke(completed);
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            PairingBeacon commit;
            PairingBeacon? reveal = null;
            PairingApproval? approval = null;
            lock (_gate)
            {
                commit = new PairingBeacon(1, PairingProtocol.BeaconService, _instanceId, _host,
                    _startedAtUnixMs, _commitment);
                if (_remoteCommit != null)
                    reveal = commit with { TargetId = _remoteCommit.InstanceId,
                        PublicKey = Convert.ToBase64String(_publicKey), Nonce = Convert.ToBase64String(_nonce) };
                if (_localApproved && _candidate != null) approval = PairingProtocol.CreateApproval(_instanceId, _candidate);
            }
            try
            {
                await SendPacketAsync(PairingProtocol.Encode(commit), cancellationToken);
                if (reveal != null) await SendPacketAsync(PairingProtocol.Encode(reveal), cancellationToken);
                if (approval != null) await SendPacketAsync(PairingProtocol.Encode(approval), cancellationToken);
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException) { }
            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
        }
    }

    private async Task SendPacketAsync(byte[] payload, CancellationToken cancellationToken)
    {
        if (_client == null) throw new InvalidOperationException("Pairing has not started.");
        await _client.SendAsync(payload, new IPEndPoint(PairingProtocol.MulticastAddress, PairingProtocol.Port), cancellationToken);
        await _client.SendAsync(payload, new IPEndPoint(IPAddress.Broadcast, PairingProtocol.Port), cancellationToken);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var packet = await _client!.ReceiveAsync(cancellationToken);
                if (PairingProtocol.TryDecodeApproval(packet.Buffer, out var approval))
                {
                    HandleApproval(approval!);
                    continue;
                }
                if (!PairingProtocol.TryDecode(packet.Buffer, out var beacon) || beacon!.InstanceId == _instanceId) continue;
                HandleBeacon(beacon);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is SocketException or CryptographicException or InvalidOperationException) { }
        }
    }

    private void HandleBeacon(PairingBeacon beacon)
    {
        PairingCandidate? found = null;
        lock (_gate)
        {
            if (_ignored.Contains(beacon.InstanceId)) return;
            if (beacon.TargetId == null)
            {
                if (_remoteCommit == null) _remoteCommit = beacon;
                return;
            }
            if (beacon.TargetId != _instanceId || _remoteCommit?.InstanceId != beacon.InstanceId || _candidate != null) return;
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(_remoteCommit.Commitment),
                    Convert.FromBase64String(beacon.Commitment))) return;
            found = PairingProtocol.Derive(_instanceId, _host, _startedAtUnixMs, _publicKey, _nonce, _key, beacon);
            _candidate = found;
        }
        if (found != null) CandidateFound?.Invoke(found);
    }

    private void HandleApproval(PairingApproval approval)
    {
        PairingCandidate? completed = null;
        lock (_gate)
        {
            if (_candidate == null || !PairingProtocol.VerifyApproval(_instanceId, _candidate, approval)) return;
            _peerApproved = true;
            if (_localApproved && Interlocked.Exchange(ref _completionRaised, 1) == 0) completed = _candidate;
        }
        if (completed != null) PairingCompleted?.Invoke(completed);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _client?.Dispose();
        foreach (var task in new[] { _sendLoop, _receiveLoop })
        {
            if (task == null) continue;
            try { await task.WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception) { }
        }
        _key.Dispose();
        _stop.Dispose();
    }
}
