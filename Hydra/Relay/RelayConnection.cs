using Cathedral.Utils;
using Common;
using Common.DTO;
using Common.Interfaces;
using System.Diagnostics;
using Hydra.Config;
using Hydra.Discovery;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Threading.Channels;
using TypedSignalR.Client;
using StyxConstants = Styx.Constants;

namespace Hydra.Relay;

public class RelayConnection(IHydraProfile profile, ILogger<RelayConnection> log, IWorldState peerState)
    : SimpleHostedService(log), IStyxClient, IRelaySender
{
    private IStyxServer? _server;
    private RelayEncryption? _encryption;

    // Loss-tolerant mouse motion has a one-frame latest-value lane. Control/key messages
    // are never discarded, while bulk file frames use an awaited bounded lane (~16 MiB).
    private readonly Channel<(string[] Targets, byte[] Payload)> _controlQueue =
        Channel.CreateUnbounded<(string[], byte[])>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<(string[] Targets, byte[] Payload)> _mouseQueue =
        Channel.CreateBounded<(string[], byte[])>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly Channel<(string[] Targets, byte[] Payload)> _reliableQueue =
        Channel.CreateBounded<(string[], byte[])>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly SemaphoreSlim _sendSignal = new(0);
    private int _mousePending;

    protected virtual TimeSpan ReconnectDelay => TimeSpan.FromMilliseconds(Constants.ReconnectDelayMilliseconds);

    // RR5: ±25% jitter so peers that all dropped at once (e.g. a relay restart) don't reconnect in lockstep
    private static TimeSpan WithJitter(TimeSpan baseDelay)
    {
        var offsetMs = (Random.Shared.NextDouble() * 2 - 1) * baseDelay.TotalMilliseconds * 0.25;
        return baseDelay + TimeSpan.FromMilliseconds(offsetMs);
    }

    // IRelaySender
    public bool IsConnected => _server != null;
    public event Func<string[], Task>? PeersChanged;
    public event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived;
    public event Func<Task>? Disconnected;

    public void Send(string[] targetHosts, byte[] payload)
    {
        OnSent(targetHosts, payload);
        if (_server == null || _encryption == null) return;
        if (payload.Length > 0 && payload[0] == (byte)MessageKind.MouseMove)
        {
            _mouseQueue.Writer.TryWrite((targetHosts, payload));
            if (Interlocked.Exchange(ref _mousePending, 1) == 0) _sendSignal.Release();
        }
        else if (_controlQueue.Writer.TryWrite((targetHosts, payload)))
        {
            _sendSignal.Release();
        }
    }

    public async ValueTask SendReliableAsync(string[] targetHosts, byte[] payload, CancellationToken cancellationToken = default)
    {
        OnSent(targetHosts, payload);
        while (await _reliableQueue.Writer.WaitToWriteAsync(cancellationToken))
        {
            if (_server == null || _encryption == null)
                throw new InvalidOperationException("Relay is disconnected.");
            if (!_reliableQueue.Writer.TryWrite((targetHosts, payload))) continue;
            _sendSignal.Release();
            return;
        }
        throw new InvalidOperationException("Relay send queue is closed.");
    }

    protected virtual void OnSent(string[] targetHosts, byte[] payload) { }

    // IStyxClient
    public async Task Receive(string sourceHost, string sourceIp, byte[] payload)
    {
        if (_encryption == null) return;

        byte[] decrypted;
        try
        {
            decrypted = await _encryption.Decrypt(sourceHost, payload, log);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not decrypt message from {SourceHost} — discarding (wrong key or malicious sender)", sourceHost);
            return;
        }

        try
        {
            var decoded = MessageSerializer.Decode(decrypted);
            if (log.IsEnabled(LogLevel.Trace))
                log.LogTrace("Received {Kind} from {SourceHost} ({Bytes} bytes)", decoded.Kind, sourceHost, payload.Length);
            await OnReceive(sourceHost, decoded.Kind, decoded.Bytes);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to decode message from {SourceHost}", sourceHost);
        }
    }

    public async Task Kicked(string reason)
    {
        log.LogWarning("Kicked from relay: {Reason}", reason);
        await OnKicked(reason);
    }

    public async Task Peers(string[] hostNames)
    {
        log.LogInformation("Peers online: {Peers}", hostNames.Length == 0 ? "(none)" : string.Join(", ", hostNames));
        await OnPeers(hostNames);
    }

    // override in subclasses (e.g. tests, slave mode)
    protected virtual async Task OnReceive(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        if (MessageReceived != null) await MessageReceived(sourceHost, kind, body);
        else await ValueTask.CompletedTask;
    }

    protected virtual async Task OnPeers(string[] hostNames)
    {
        if (PeersChanged != null) await PeersChanged(hostNames);
        else await ValueTask.CompletedTask;
    }

    protected virtual Task OnKicked(string reason) => Task.CompletedTask;
    // fires after _server and _encryption are set — guaranteed connection-ready signal
    protected virtual Task OnAuthenticated() => Task.CompletedTask;
    // per-connection cancellation token: cancels when this connection drops. Valid only during
    // OnAuthenticated (the source CTS is disposed once the connection loop unwinds, before OnDisconnected).
    protected CancellationToken ConnectionToken { get; private set; }
    // fires when a live connection drops (not on auth failure or clean shutdown)
    protected virtual Task OnDisconnected() => Task.CompletedTask;

    // override in tests to inject the in-memory handler; production default sets NoDelay
    protected virtual void ConfigureHubUrl(HttpConnectionOptions options)
    {
        options.HttpMessageHandlerFactory = _ => new SocketsHttpHandler
        {
            ConnectCallback = async (ctx, cancel) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(ctx.DnsEndPoint, cancel);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
    }

    protected override async Task Execute(CancellationToken cancel)
    {
        if (profile.NetworkConfig == null) return;

        NetworkConfig netConfig;
        try
        {
            netConfig = NetworkConfig.Parse(profile.NetworkConfig);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to parse NetworkConfig — relay disabled");
            return;
        }

        var hostName = profile.Name;
        log.LogInformation("Starting relay connection to {Server} as {HostName}", netConfig.StyxServer, hostName);
        var retryDelay = ReconnectDelay;

        while (!cancel.IsCancellationRequested)
        {
            var completedAuthenticatedSession = false;
            try
            {
                var attemptStarted = Stopwatch.GetTimestamp();
                var connectionConfig = netConfig;
                if (netConfig.StyxServer.StartsWith("auto://", StringComparison.OrdinalIgnoreCase))
                {
                    var deskName = netConfig.StyxServer[7..];
                    using var discoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    discoveryTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    log.LogInformation("Discovering ScreenFuse desk {Desk} on the LAN", deskName);
                    var discovered = await LanDiscovery.FindServerAsync(deskName, netConfig.EncryptionKey, discoveryTimeout.Token);
                    log.LogInformation("Discovered ScreenFuse relay at {Server}", discovered);
                    connectionConfig = netConfig with { StyxServer = discovered };
                }
                var authenticated = await Connect(connectionConfig, hostName, cancel);
                completedAuthenticatedSession = authenticated && Stopwatch.GetElapsedTime(attemptStarted) >= TimeSpan.FromSeconds(5);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                log.LogWarning("Relay connection lost");
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning("Relay connection failed: {Message}", ex.InnerException?.Message ?? ex.Message);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Relay connection failed");
            }
            finally
            {
                var wasConnected = _server != null;
                _server = null;
                _encryption = null;
                while (_controlQueue.Reader.TryRead(out _)) { }
                while (_mouseQueue.Reader.TryRead(out _)) { }
                while (_reliableQueue.Reader.TryRead(out _)) { }
                Interlocked.Exchange(ref _mousePending, 0);
                while (_sendSignal.Wait(0)) { }
                if (wasConnected)
                {
                    // guard the disconnect callbacks: a throw here would escape Execute, and because the
                    // base SimpleHostedService has no exceptionLoopTime it would permanently kill the
                    // reconnect loop (silent, until process restart). Log and keep reconnecting instead.
                    try
                    {
                        await OnDisconnected();
                        if (Disconnected != null) await Disconnected();
                    }
                    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Error handling relay disconnect — continuing to reconnect");
                    }
                }
            }

            if (!cancel.IsCancellationRequested)
            {
                if (completedAuthenticatedSession) retryDelay = ReconnectDelay;
                log.LogInformation("Retrying relay in {ReconnectDelay:F2}s", retryDelay.TotalSeconds);
                await Task.Delay(WithJitter(retryDelay), cancel).ConfigureAwait(false);
                if (!completedAuthenticatedSession)
                    retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, TimeSpan.FromSeconds(Constants.ReconnectMaxDelaySeconds).TotalMilliseconds));
            }
        }
    }

    private async Task<bool> Connect(NetworkConfig netConfig, string hostName, CancellationToken cancel)
    {
        using var disco = CancellationTokenSource.CreateLinkedTokenSource(cancel);

        await using var con = new HubConnectionBuilder()
            .WithUrl($"{netConfig.StyxServer}/relay", ConfigureHubUrl)
            .WithKeepAliveInterval(TimeSpan.FromSeconds(StyxConstants.KeepAliveSeconds))
            .WithServerTimeout(TimeSpan.FromSeconds(StyxConstants.ClientTimeoutSeconds))
            .AddMessagePackProtocol()
            .Build();

        // ReSharper disable once AccessToDisposedClosure
        con.Closed += async _ =>
        {
            try { await disco.CancelAsync(); }
            catch (ObjectDisposedException) { }
        };

        await con.StartAsync(disco.Token);
        log.LogInformation("Connected to Styx relay");

        var server = con.CreateHubProxy<IStyxServer>(cancellationToken: disco.Token);
        using var reg = con.Register<IStyxClient>(this);

        // set before Authenticate so messages arriving during the auth handshake aren't dropped:
        // Styx broadcasts Peers (triggering MasterConfig from master) before returning Authenticated=true,
        // so _encryption must be ready to decrypt that incoming message
        _encryption = new RelayEncryption(netConfig.EncryptionKey, peerState);
        _server = server;

        // Paired/embedded relays require a fresh proof bound to this connection. Older standalone
        // Styx deployments can explicitly advertise legacy compatibility during migration.
        RelayLoginResponse response;
        try
        {
            var challenge = await server.BeginAuthenticate()
                .WaitAsync(TimeSpan.FromSeconds(Constants.AuthTimeoutSeconds), disco.Token);
            if (challenge.AllowsLegacy)
            {
                response = await AuthenticateLegacy(server, netConfig.Authorization, hostName, disco.Token);
            }
            else
            {
                var login = new RelayLoginV2
                {
                    Authorization = netConfig.Authorization,
                    HostName = hostName,
                    ChallengeId = challenge.ChallengeId,
                    Proof = RelayAuthProof.Compute(netConfig.EncryptionKey, challenge, netConfig.Authorization,
                        hostName, con.ConnectionId ?? throw new InvalidOperationException("Relay connection has no id.")),
                };
                response = await server.AuthenticateV2(login)
                    .WaitAsync(TimeSpan.FromSeconds(Constants.AuthTimeoutSeconds), disco.Token);
            }
        }
        catch (Exception ex) when (ex is HubException or MissingMethodException)
        {
            log.LogDebug("Relay does not support challenge authentication; trying legacy authentication");
            response = await AuthenticateLegacy(server, netConfig.Authorization, hostName, disco.Token);
        }

        if (!response.Authenticated)
        {
            _server = null;
            _encryption = null;
            log.LogError("Relay authentication failed: {Message}", response.Message);
            return false;
        }

        log.LogInformation("Authenticated on relay as {HostName}", hostName);
        // R5: per-connection token (cancels when this connection drops), NOT the app-lifetime token — so
        // awaiters like WaitForAccessibilityTrusted in OnAuthenticated unwind on a drop and reconnect.
        ConnectionToken = disco.Token;
        await OnAuthenticated();

        // Drain with input priority. Mouse is latest-value only; reliable file frames
        // make progress one at a time so keyboard/button traffic can preempt them.
        while (true)
        {
            await _sendSignal.WaitAsync(disco.Token);
            (string[] Targets, byte[] Payload) item;
            if (!_controlQueue.Reader.TryRead(out item) &&
                !TryReadMouse(out item) &&
                !_reliableQueue.Reader.TryRead(out item))
                continue;

            try
            {
                var encrypted = await _encryption.Encrypt(item.Payload, cancel);
                await _server.Send(item.Targets, encrypted);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpRequestException ex)
            {
                log.LogWarning("Failed to send relay message to [{TargetHosts}]: {Message}", string.Join(", ", item.Targets), ex.InnerException?.Message ?? ex.Message);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to send relay message to [{TargetHosts}]", string.Join(", ", item.Targets));
            }
        }
        return true;
    }

    private static Task<RelayLoginResponse> AuthenticateLegacy(IStyxServer server, string authorization,
        string hostName, CancellationToken cancellationToken) => server.Authenticate(new RelayLogin
        {
            Authorization = authorization,
            HostName = hostName,
        }).WaitAsync(TimeSpan.FromSeconds(Constants.AuthTimeoutSeconds), cancellationToken);

    private bool TryReadMouse(out (string[] Targets, byte[] Payload) item)
    {
        Interlocked.Exchange(ref _mousePending, 0);
        return _mouseQueue.Reader.TryRead(out item);
    }
}
