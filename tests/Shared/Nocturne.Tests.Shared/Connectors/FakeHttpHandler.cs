using System.Net;
using System.Text;

namespace Nocturne.Tests.Shared.Connectors;

/// <summary>
///     A vendor's HTTP API for a connector test. A request goes to the first route whose method and
///     URL fragment match, else to <see cref="Otherwise"/>, else it answers 404. A route holding
///     several answers gives them in turn and repeats the last, so a test can fail a call once and
///     then succeed. Every request is recorded with its body, read inside the handler because the
///     caller disposes the message once the send completes.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly List<Route> _routes = [];
    private readonly List<RecordedRequest> _requests = [];
    private readonly object _gate = new();

    public FakeHttpHandler()
    {
    }

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> otherwise) => Otherwise = otherwise;

    /// <summary>The answer to a request no route claims.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? Otherwise { get; set; }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate) return [.. _requests];
        }
    }

    public int CallsTo(string urlFragment) =>
        Requests.Count(r => r.Url.Contains(urlFragment, StringComparison.OrdinalIgnoreCase));

    public FakeHttpHandler On(string urlFragment, params Func<HttpRequestMessage, HttpResponseMessage>[] answers) =>
        Add(null, urlFragment, [.. answers.Select(Completed)]);

    public FakeHttpHandler On(HttpMethod method, string urlFragment, params Func<HttpRequestMessage, HttpResponseMessage>[] answers) =>
        Add(method, urlFragment, [.. answers.Select(Completed)]);

    /// <summary>
    ///     A route answering with copies of ready-made responses, because a caller disposes the
    ///     response it is given and a route may be asked again.
    /// </summary>
    public FakeHttpHandler On(string urlFragment, params HttpResponseMessage[] answers) =>
        Add(null, urlFragment, [.. answers.Select(Replay)]);

    /// <summary>A route whose answer waits, for a test that cancels or interleaves while the request is in flight.</summary>
    public FakeHttpHandler OnAsync(string urlFragment, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) =>
        Add(null, urlFragment, [answer]);

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body) };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var url = request.RequestUri!.ToString();
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? answer;
        lock (_gate)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri, url, body));
            var route = _routes.FirstOrDefault(r => r.Matches(request.Method, url));
            answer = route?.Next() ?? (Otherwise is null ? null : Completed(Otherwise));
        }

        return answer is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : await answer(request, cancellationToken);
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Replay(
        HttpResponseMessage template) =>
        async (_, cancellationToken) =>
        {
            var copy = new HttpResponseMessage(template.StatusCode);
            foreach (var header in template.Headers)
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (template.Content is not null)
            {
                copy.Content = new ByteArrayContent(await template.Content.ReadAsByteArrayAsync(cancellationToken));
                foreach (var header in template.Content.Headers)
                    copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return copy;
        };

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Completed(
        Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        (request, _) => Task.FromResult(answer(request));

    private FakeHttpHandler Add(
        HttpMethod? method, string urlFragment, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] answers)
    {
        lock (_gate) _routes.Add(new Route(method, urlFragment, answers));
        return this;
    }

    private sealed class Route(
        HttpMethod? method, string urlFragment, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] answers)
    {
        private int _next;

        public bool Matches(HttpMethod requested, string url) =>
            (method is null || method == requested)
            && url.Contains(urlFragment, StringComparison.OrdinalIgnoreCase);

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Next() =>
            answers[Math.Min(_next++, answers.Length - 1)];
    }
}

public sealed record RecordedRequest(HttpMethod Method, Uri? Uri, string Url, string Body);
