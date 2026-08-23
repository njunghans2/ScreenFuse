using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Scenes;

public sealed class SceneControlServer(
    ISceneCoordinator scenes,
    Hydra.Desk.IDeskService desk,
    int port,
    ILogger<SceneControlServer> log) : BackgroundService
{
    private readonly HttpListener _listener = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            _listener.Start();
            log.LogInformation("ScreenFuse control page: http://127.0.0.1:{Port}/", port);
        }
        catch (HttpListenerException ex)
        {
            log.LogWarning(ex, "Could not start ScreenFuse control page on port {Port}", port);
            return;
        }

        using var registration = stoppingToken.Register(() => _listener.Close());
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(stoppingToken);
                _ = Task.Run(() => HandleAsync(context, stoppingToken), stoppingToken);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var request = context.Request;
            if (!request.UserHostName.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, 403, new { message = "Invalid control host." }, cancellationToken);
                return;
            }
            var origin = request.Headers["Origin"];
            if (origin != null && !IsLoopbackOrigin(origin, port))
            {
                await WriteJsonAsync(context.Response, 403, new { message = "Cross-origin control requests are not allowed." }, cancellationToken);
                return;
            }
            if (request.HttpMethod == "GET" && request.Url?.AbsolutePath is "/" or "/api/status")
            {
                // The whole desk as this computer sees it. Both computers answer the same question
                // the same way, so the two can be compared directly — which is the only way to tell
                // "the desk is wrong" apart from "the desk is fine and this computer disagrees".
                var snapshot = desk.Snapshot;
                await WriteJsonAsync(context.Response, 200, new
                {
                    currentScene = scenes.CurrentScene,
                    scenes = scenes.AvailableScenes,
                    connectedPeers = scenes.ConnectedPeers,
                    expectedPeers = scenes.ExpectedPeers,
                    localHost = snapshot.LocalHost,
                    controller = snapshot.Controller,
                    isController = snapshot.IsController,
                    hosts = snapshot.Hosts,
                    deskConnected = snapshot.ConnectedHosts,
                    monitors = snapshot.Monitors.Select(m => new
                    {
                        m.Id,
                        m.Label,
                        m.DeskX,
                        m.DeskY,
                        m.Width,
                        m.Height,
                        m.ActiveHost,
                        m.Switchable,
                        sources = m.Sources.Select(s => new { s.Host, s.Input, s.Reachable, s.AvailableInputs }),
                    }),
                    crossings = snapshot.Crossings,
                }, cancellationToken);
                return;
            }

            // The same answer as plain text, staged in the order the stages depend on each other.
            // Written here rather than in a script because both computers must report it identically
            // and neither can be assumed to have anything installed to read JSON with.
            if (request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/api/status.txt")
            {
                await WriteAsync(context.Response, 200, "text/plain; charset=utf-8", Summarise(), cancellationToken);
                return;
            }

            const string prefix = "/api/scenes/";
            if (request.HttpMethod == "POST" && request.Url?.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) == true)
            {
                var name = Uri.UnescapeDataString(request.Url.AbsolutePath[prefix.Length..]);
                var result = await scenes.ActivateAsync(name, cancellationToken);
                await WriteJsonAsync(context.Response, result.Accepted ? 202 : 400, result, cancellationToken);
                return;
            }

            await WriteJsonAsync(context.Response, 404, new { message = "Not found" }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Control API request failed");
            if (context.Response.OutputStream.CanWrite)
                await WriteJsonAsync(context.Response, 500, new { message = ex.Message }, CancellationToken.None);
        }
    }

    private string Summarise()
    {
        var s = desk.Snapshot;
        var text = new StringBuilder();
        void Line(bool good, string what) => text.AppendLine($"  {(good ? "ok  " : "FAIL")}  {what}");

        text.AppendLine($"computer: {s.LocalHost}   controller: {s.Controller}   role: {(s.IsController ? "has the keyboard" : "follows")}");

        if (!s.Ready)
        {
            text.AppendLine("  WAIT  the desk has not finished a round yet -- nothing below is settled,");
            text.AppendLine("        and the role above is a placeholder rather than a fact");
        }
        text.AppendLine("1. display management");
        Line(s.Monitors.Count > 0, $"{s.Monitors.Count} monitor(s) on the desk");
        foreach (var m in s.Monitors)
            text.AppendLine($"        {m.Label}  {m.Width}x{m.Height}  at {m.DeskX},{m.DeskY}  on={m.ActiveHost ?? "nobody"}");

        text.AppendLine("2. connection");
        var others = s.Hosts.Where(h => !h.Equals(s.LocalHost, StringComparison.OrdinalIgnoreCase)).ToList();
        Line(others.Count == 0 || s.ConnectedHosts.Count > 0,
            others.Count == 0 ? "no other computer configured" : $"connected: {string.Join(", ", s.ConnectedHosts.DefaultIfEmpty("(none)"))} of {string.Join(", ", others)}");

        text.AppendLine("3. cursor");
        var crossings = s.Crossings ?? [];
        Line(crossings.Count > 0, crossings.Count > 0 ? $"{crossings.Count} crossing(s)" : "no crossings — the pointer cannot leave this computer");
        foreach (var c in crossings) text.AppendLine($"        {c}");

        text.AppendLine("4. arrangement");
        text.AppendLine($"        layout: {string.Join("; ", s.Monitors.Select(m => $"{m.Id}@{m.DeskX},{m.DeskY}"))}");
        text.AppendLine($"        crossings: {string.Join("; ", crossings)}");

        text.AppendLine("5. switching");
        var shared = s.Monitors.Where(m => m.Sources.Count > 1).ToList();
        if (shared.Count == 0) text.AppendLine("        no monitor is wired to more than one computer yet");
        foreach (var m in shared)
        {
            var known = m.Sources.Count(x => x.Input != null);
            Line(known == m.Sources.Count,
                $"{m.Label}: {string.Join(", ", m.Sources.Select(x => $"{x.Host}={x.Input?.ToString() ?? "unknown"}"))}");
        }
        return text.ToString();
    }

    private static Task WriteJsonAsync(HttpListenerResponse response, int status, object body, CancellationToken cancellationToken) =>
        WriteAsync(response, status, "application/json", JsonSerializer.Serialize(body), cancellationToken);

    private static async Task WriteAsync(HttpListenerResponse response, int status, string contentType, string body, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.Close();
    }

    private static bool IsLoopbackOrigin(string origin, int port) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && IPAddress.IsLoopback(uri.HostNameType == UriHostNameType.IPv6
            ? IPAddress.Parse(uri.Host)
            : IPAddress.TryParse(uri.Host, out var address) ? address : IPAddress.None) && uri.Port == port;
}
