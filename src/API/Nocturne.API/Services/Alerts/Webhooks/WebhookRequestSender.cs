using System.Text;

namespace Nocturne.API.Services.Alerts.Webhooks;

public class WebhookRequestSender(
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookRequestSender> logger)
{
    public async Task<IReadOnlyList<string>> SendAsync(
        IEnumerable<string> urls,
        string payload,
        string? secret,
        CancellationToken cancellationToken = default
    )
    {
        var httpClient = httpClientFactory.CreateClient();
        var failures = new List<string>();
        var urlList = urls.Where(url => !string.IsNullOrWhiteSpace(url)).ToList();

        foreach (var url in urlList)
        {
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
