using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// Webhook notification settings for alert delivery.
/// </summary>
public class WebhookNotificationSettings
{
    /// <summary>
    /// Whether webhook delivery is enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>
    /// URLs to notify.
    /// </summary>
    [JsonPropertyName("urls")]
    public List<string> Urls { get; init; } = [];

    /// <summary>
    /// Optional shared secret for webhook signatures.
    /// </summary>
    [JsonPropertyName("secret")]
    public string? Secret { get; init; }

    /// <summary>
    /// Indicates whether a secret is already configured.
    /// </summary>
    [JsonPropertyName("hasSecret")]
    public bool HasSecret { get; init; }

    /// <summary>
    /// Signature version sent with webhook requests.
    /// </summary>
    [JsonPropertyName("signatureVersion")]
    public string SignatureVersion { get; init; } = "v1";
}
