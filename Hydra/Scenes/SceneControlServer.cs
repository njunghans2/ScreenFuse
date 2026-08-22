using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Scenes;

public sealed class SceneControlServer(ISceneCoordinator scenes, int port, ILogger<SceneControlServer> log) : BackgroundService
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
                await WriteJsonAsync(context.Response, 200, new { currentScene = scenes.CurrentScene, scenes = scenes.AvailableScenes, connectedPeers = scenes.ConnectedPeers, expectedPeers = scenes.ExpectedPeers }, cancellationToken);
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
