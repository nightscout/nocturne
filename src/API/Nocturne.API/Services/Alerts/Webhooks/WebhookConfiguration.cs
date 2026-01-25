using System.Text.Json;

namespace Nocturne.API.Services.Alerts.Webhooks;

public sealed record WebhookConfiguration(IReadOnlyList<string> Urls, string? Secret);

public static class WebhookConfigurationParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static WebhookConfiguration Parse(string? json, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new WebhookConfiguration([], null);
        }

        try
        {
            var payload = JsonSerializer.Deserialize<WebhookConfigurationPayload>(json, JsonOptions);
            if (payload?.Urls != null)
            {
                return new WebhookConfiguration(
                    NormalizeUrls(payload.Urls),
                    NormalizeSecret(payload.Secret)
                );
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Webhook config payload was not an object");
        }

        try
        {
            var urls = JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? Array.Empty<string>();
            return new WebhookConfiguration(NormalizeUrls(urls), null);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Webhook URLs payload could not be parsed");
        }

        return new WebhookConfiguration([], null);
    }

    public static string Serialize(IReadOnlyCollection<string> urls, string? secret)
    {
        var payload = new WebhookConfigurationPayload
        {
            Urls = NormalizeUrls(urls).ToArray(),
            Secret = NormalizeSecret(secret),
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static IReadOnlyList<string> NormalizeUrls(IEnumerable<string> urls)
    {
        return urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? NormalizeSecret(string? secret)
    {
        return string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
    }

    private sealed class WebhookConfigurationPayload
    {
        public string[]? Urls { get; init; }
        public string? Secret { get; init; }
    }
}
