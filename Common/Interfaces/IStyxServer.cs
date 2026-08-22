using Common.DTO;

namespace Common.Interfaces;

public interface IStyxServer
{
    Task<RelayAuthChallenge> BeginAuthenticate();
    Task<RelayLoginResponse> AuthenticateV2(RelayLoginV2? login);
    // nullable because a peer can send nil where the login should be; the relay refuses it like any other
    // incomplete login rather than faulting the invocation
    Task<RelayLoginResponse> Authenticate(RelayLogin? login);
    Task<bool> Ping();
    Task<string> GetMyIp();
    Task Send(string[] targetHosts, byte[] payload);
}
