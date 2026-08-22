namespace Hydra.Relay;

internal static class Constants
{
    public const int ReconnectDelayMilliseconds = 250;
    public const int ReconnectMaxDelaySeconds = 5;
    public const int AuthTimeoutSeconds = 10; // cap the Authenticate round-trip so a stalled handshake retries
}
