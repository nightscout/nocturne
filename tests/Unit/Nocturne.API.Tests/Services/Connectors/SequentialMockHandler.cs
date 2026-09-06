using System.Net;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Mock handler that returns pre-queued responses in order and records request URLs.
/// An exhausted queue answers an empty JSON array (a cleanly exhausted range).
/// </summary>
internal class SequentialMockHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<string> RequestUrls { get; } = [];

    public void Enqueue(HttpResponseMessage response) =>
        _responses.Enqueue(response);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestUrls.Add(request.RequestUri?.PathAndQuery ?? "");

        if (_responses.Count == 0)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            });

        return Task.FromResult(_responses.Dequeue());
    }
}
