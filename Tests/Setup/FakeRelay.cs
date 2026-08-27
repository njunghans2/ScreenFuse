using System.Text;
using Hydra.Relay;

namespace Tests.Setup;

public sealed class FakeRelay : IRelaySender
{
    public readonly List<(string[] Targets, MessageKind Kind, string Json)> Sent = [];
    public bool IsConnected { get; set; } = true;
    public event Func<string[], Task>? PeersChanged;
    public event Func<string, MessageKind, ReadOnlyMemory<byte>, Task>? MessageReceived;
    public event Func<Task>? Disconnected;

    public void Send(string[] targetHosts, byte[] payload)
    {
        var decoded = MessageSerializer.Decode(payload);
        Sent.Add((targetHosts, decoded.Kind, decoded.Json));
    }

    // set to InputRouter.FlushAsync, the same way FakePlatform is. Handling a relay message can
    // hand work to the router's queues and return before any of it has run, and a test that then
    // looks at what was sent would be looking too early.
    public Func<Task>? AfterFireCallback { get; set; }

    private async Task Settle()
    {
        if (AfterFireCallback != null) await AfterFireCallback();
    }

    public async Task FirePeersChanged(params string[] hosts)
    {
        if (PeersChanged != null) await PeersChanged(hosts);
        await Settle();
    }

    public async Task FireMessageReceived(string host, MessageKind kind, string json)
    {
        if (MessageReceived != null) await MessageReceived(host, kind, Encoding.UTF8.GetBytes(json));
        await Settle();
    }

    public async Task FireDisconnected()
    {
        if (Disconnected != null) await Disconnected();
        await Settle();
    }
}
