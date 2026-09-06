using System.Net;

namespace Nocturne.Connectors.Nightscout.Tests.TestSupport;

/// <summary>
/// Captures every outbound request (method, URI, headers, serialized body) and replies
/// with a caller-chosen status. Bodies are read inside the handler because the sink
/// disposes the <see cref="HttpRequestMessage"/> as soon as the send completes.
/// </summary>
internal sealed class RecordingHttpMessageHandler(HttpStatusCode statusCode = HttpStatusCode.OK)
    : HttpMessageHandler
{
    public List<HttpMethod> Methods { get; } = [];

    public List<Uri> Uris { get; } = [];

    public List<string> ApiSecretHeaders { get; } = [];

    public List<string> Bodies { get; } = [];

    public int RequestCount => Uris.Count;

    /// <summary>
    /// Returns an exception to throw instead of responding, keyed by 1-based ordinal.
    /// The request is recorded before the throw, so a thrown request still counts
    /// toward <see cref="RequestCount"/> — matching a transport failure that reached
    /// the wire.
    /// </summary>
    public Func<int, Exception?>? ThrowFor { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // A real handler observes the token before it touches the network.
        cancellationToken.ThrowIfCancellationRequested();

        Methods.Add(request.Method);
        Uris.Add(request.RequestUri!);
        ApiSecretHeaders.Add(
            request.Headers.TryGetValues("api-secret", out var values)
                ? string.Join(",", values)
                : string.Empty);
        Bodies.Add(
            request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

        var toThrow = ThrowFor?.Invoke(Uris.Count);
        if (toThrow is not null)
            throw toThrow;

        return new HttpResponseMessage(statusCode);
    }
}
