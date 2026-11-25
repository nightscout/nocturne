using System.Net.WebSockets;
using SocketIOClient.Protocol.WebSocket;
using WebSocketMessageType = System.Net.WebSockets.WebSocketMessageType;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// The WebSocket SocketIOClient upgrades to, opened through the handlers of the named
/// <see cref="IHttpClientFactory"/> client <paramref name="clientName"/> in place of a bare
/// <see cref="ClientWebSocket"/>. The upgrade request then passes the same link-local guard, pinned
/// connector and telemetry mark as the client's polling requests.
/// </summary>
/// <remarks>
/// The factory owns the handler, so the invoker does not dispose it. A connection that has switched
/// protocols leaves the handler's pool, so the factory retiring that handler does not close it.
/// </remarks>
internal sealed class ConnectorClientWebSocket(IHttpMessageHandlerFactory handlers, string clientName)
    : IWebSocketClient
{
    private readonly ClientWebSocket _ws = new();

    public WebSocketState State => _ws.State;

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var invoker = new HttpMessageInvoker(handlers.CreateHandler(clientName), disposeHandler: false);
        await _ws.ConnectAsync(uri, invoker, cancellationToken);
    }

    public Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken) =>
        _ws.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
        _ws.ReceiveAsync(buffer, cancellationToken);

    public Task CloseAsync(WebSocketCloseStatus closeStatus, string desc, CancellationToken cancellationToken) =>
        _ws.CloseAsync(closeStatus, desc, cancellationToken);

    public void SetDefaultHeader(string name, string value) => _ws.Options.SetRequestHeader(name, value);

    public void Dispose() => _ws.Dispose();
}
