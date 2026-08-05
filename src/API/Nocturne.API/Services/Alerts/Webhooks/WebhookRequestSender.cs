using System.Text;
using Nocturne.Core.Models.Net;

namespace Nocturne.API.Services.Alerts.Webhooks;

/// <summary>
/// Sends JSON webhook payloads via HTTP POST to one or more URLs, optionally signing
/// each request with an HMAC-SHA256 signature via <see cref="WebhookSignature"/>.
/// </summary>
/// <remarks>
/// Blank or null URLs are silently skipped. Failed URLs are collected and returned
/// rather than raising an exception so that partial success is possible. A URL that is
/// not a publicly routable http(s) destination is reported as failed without being
/// requested — see <see cref="OutboundDestination"/>.
/// </remarks>
public class WebhookRequestSender(
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookRequestSender> logger)
{
    /// <summary>
    /// Name of the HTTP client used for webhook delivery. Configured not to follow
    /// redirects, so <see cref="OutboundDestination"/>'s verdict on the URL cannot be
    /// walked past by the target answering 3xx.
    /// </summary>
    public const string HttpClientName = "Webhook";

    /// <summary>
    /// Posts <paramref name="payload"/> to each URL in <paramref name="urls"/>.
    /// </summary>
    /// <param name="urls">The destination webhook URLs to POST to.</param>
    /// <param name="payload">The JSON payload to include in the request body.</param>
    /// <param name="secret">
    /// Optional HMAC signing secret. When non-null the request includes
    /// <c>X-Nocturne-Timestamp</c>, <c>X-Nocturne-Signature</c>, and
    /// <c>X-Nocturne-Signature-Version</c> headers computed by <see cref="WebhookSignature"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A read-only list of URLs that failed to receive the payload (non-2xx response or exception).
    /// An empty list indicates full success.
    /// </returns>
    public async Task<IReadOnlyList<string>> SendAsync(
        IEnumerable<string> urls,
        string payload,
        string? secret,
        CancellationToken cancellationToken = default
    )
    {
        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        var failures = new List<string>();
        var urlList = urls.Where(url => !string.IsNullOrWhiteSpace(url)).ToList();

        foreach (var url in urlList)
        {
            if (!await OutboundDestination.IsPubliclyRoutableAsync(url, cancellationToken))
            {
                failures.Add(url);
                logger.LogWarning(
                    "Refusing to send webhook to {Url}: not a publicly routable http(s) destination", url);
                continue;
            }

            try
            {
                using var content = new StringContent(
                    payload,
                    Encoding.UTF8,
                    "application/json"
                );

                if (!string.IsNullOrWhiteSpace(secret))
                {
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var signature = WebhookSignature.Create(secret, payload, timestamp);
                    content.Headers.Add("X-Nocturne-Timestamp", timestamp.ToString());
                    content.Headers.Add("X-Nocturne-Signature", signature);
                    content.Headers.Add("X-Nocturne-Signature-Version", "v1");
                }

                var response = await httpClient.PostAsync(url, content, cancellationToken);
                if (response.IsSuccessStatusCode) continue;
                failures.Add(url);
                logger.LogWarning(
                    "Webhook delivery failed for {Url} with status {StatusCode}",
                    url,
                    response.StatusCode
                );
            }
            catch (Exception ex)
            {
                failures.Add(url);
                logger.LogWarning(ex, "Failed to send webhook to {Url}", url);
            }
        }

        return failures;
    }
}
