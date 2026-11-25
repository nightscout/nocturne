using System.Net.Http.Headers;
using System.Net.WebSockets;
using SocketIOClient.Protocol.WebSocket;
using WebSocketMessageType = System.Net.WebSockets.WebSocketMessageType;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// A WebSocket SocketIOClient opens, through the handlers of a named <see cref="IHttpClientFactory"/>
/// client in place of a bare <see cref="ClientWebSocket"/>. Its handshake then passes the same
/// link-local guard, pinned connector and telemetry mark as the client's polling requests, and
/// carries the client's default headers, which an <see cref="HttpClient"/> adds and its handlers do not.
/// </summary>
/// <remarks>
/// The factory owns the handler, so the invoker does not dispose it. A connection that has switched
/// protocols leaves the handler's pool, so the factory retiring that handler does not close it.
/// </remarks>
internal sealed class ConnectorClientWebSocket : IWebSocketClient
{
    private readonly ClientWebSocket _ws = new();
    private readonly IHttpMessageHandlerFactory _handlers;
    private readonly string _clientName;

    public ConnectorClientWebSocket(
        IHttpMessageHandlerFactory handlers,
        string clientName,
        HttpRequestHeaders defaultHeaders)
    {
        _handlers = handlers;
        _clientName = clientName;
        foreach (var (name, values) in defaultHeaders.NonValidated)
            _ws.Options.SetRequestHeader(name, values.ToString());
    }

    public WebSocketState State => _ws.State;

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var invoker = new HttpMessageInvoker(_handlers.CreateHandler(_clientName), disposeHandler: false);
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
