using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Common.DTO;

public sealed class RelayAuthChallenge
{
    public required string ChallengeId { get; init; }
    public required string Nonce { get; init; }
    public required long ExpiresAtUnixMs { get; init; }
    public required bool AllowsLegacy { get; init; }
}

public sealed class RelayLoginV2
{
    public required string Authorization { get; init; }
    public required string HostName { get; init; }
    public required string ChallengeId { get; init; }
    public required string Proof { get; init; }
}

public static class RelayAuthProof
{
    public static string Compute(string credential, RelayAuthChallenge challenge, string authorization,
        string hostName, string connectionId)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(credential));
        Append(hmac, "screenfuse-relay-auth-v2"u8);
        Append(hmac, Encoding.UTF8.GetBytes(challenge.ChallengeId));
        Append(hmac, Convert.FromBase64String(challenge.Nonce));
        Append(hmac, Encoding.UTF8.GetBytes(authorization));
        Append(hmac, Encoding.UTF8.GetBytes(hostName.Trim().ToLowerInvariant()));
        Append(hmac, Encoding.UTF8.GetBytes(connectionId));
        return Convert.ToBase64String(hmac.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}
