using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>
/// An Engine.IO 4 server as far as a client's connect and upgrade. Over polling it opens a session
/// that offers the WebSocket upgrade and holds each poll until a namespace connect is posted, which
/// the poll then answers. A WebSocket request is refused with a 400 when
/// <paramref name="refuseWebSockets"/>, as by a proxy that does not pass <c>Upgrade</c> on; otherwise
/// the upgrade, or a namespace connect sent over the socket, is answered with the namespace connect and
/// a <c>dataUpdate</c>, which the site sends only over a WebSocket.
/// </summary>
internal sealed class EngineIoSite(bool refuseWebSockets = false)
{
    private readonly SemaphoreSlim _connectsToAnswer = new(0);
    private int _webSocketRequests;

    public int WebSocketRequests => Volatile.Read(ref _webSocketRequests);

    /// <summary>The <c>User-Agent</c> of the last polling request, or null before one.</summary>
    public string? PollingUserAgent { get; private set; }

    /// <summary>The <c>User-Agent</c> of the last WebSocket request, or null before one.</summary>
    public string? WebSocketUserAgent { get; private set; }

    /// <summary>Starts the site on a loopback port of its own.</summary>
    public async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var site = builder.Build();
        site.UseWebSockets();
        site.Run(AnswerAsync);
        await site.StartAsync();
        return site;
    }

    private async Task AnswerAsync(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            WebSocketUserAgent = context.Request.Headers.UserAgent;
            Interlocked.Increment(ref _webSocketRequests);
            if (refuseWebSockets)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            await AnswerWebSocketAsync(await context.WebSockets.AcceptWebSocketAsync(), context.RequestAborted);
            return;
        }

        PollingUserAgent = context.Request.Headers.UserAgent;
        context.Response.ContentType = "text/plain; charset=UTF-8";
        if (HttpMethods.IsPost(context.Request.Method))
        {
            using var body = new StreamReader(context.Request.Body);
            if ((await body.ReadToEndAsync()).StartsWith("40", StringComparison.Ordinal))
                _connectsToAnswer.Release();
            await context.Response.WriteAsync("ok");
            return;
        }

        if (!context.Request.Query.ContainsKey("sid"))
        {
            await context.Response.WriteAsync(
                "0{\"sid\":\"session\",\"upgrades\":[\"websocket\"],\"pingInterval\":25000,\"pingTimeout\":20000,\"maxPayload\":1000000}");
            return;
        }

        try
        {
            await _connectsToAnswer.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("40{\"sid\":\"namespace\"}");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task AnswerWebSocketAsync(WebSocket socket, CancellationToken aborted)
    {
        var buffer = new byte[4096];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(buffer, aborted);
                if (received.MessageType == WebSocketMessageType.Close)
                    return;

                if (Encoding.UTF8.GetString(buffer, 0, received.Count) is "5" or ['4', '0', ..])
                {
                    await SendAsync(socket, "40{\"sid\":\"namespace\"}", aborted);
                    await SendAsync(socket, "42[\"dataUpdate\",{}]", aborted);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
    }

    private static Task SendAsync(WebSocket socket, string message, CancellationToken aborted) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, aborted);
}
