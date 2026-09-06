using System.Security.Cryptography;
using System.Text.Json;
using Nocturne.API.Services.Alerts.Webhooks;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts.Providers;

/// <summary>
/// Delivers alert payloads via HTTP POST to configured webhook URLs
/// using the existing WebhookRequestSender infrastructure.
/// </summary>
internal sealed class WebhookProvider(
    WebhookRequestSender webhookSender,
    ISecretEncryptionService encryption,
    ILogger<WebhookProvider> logger)
{
    /// <summary>
    /// Delivers an alert payload to one or more webhook URLs encoded in <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">
    /// Comma-separated list of webhook URL(s) to POST the payload to.
    /// </param>
    /// <param name="encryptedSecret">
    /// The channel's signing secret as ciphertext, or null to send unsigned. Plaintext exists
    /// only for the duration of the send.
    /// </param>
    /// <param name="payload">The <see cref="AlertPayload"/> serialised as JSON for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the configured secret cannot be decrypted, or when one or more URLs fail to
    /// receive the payload.
    /// </exception>
    public async Task SendAsync(string destination, string? encryptedSecret, AlertPayload payload, CancellationToken ct)
    {
        var payloadJson = JsonSerializer.Serialize(payload);

        // Destination may contain multiple URLs separated by commas
        var urls = destination.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var failures = await webhookSender.SendAsync(urls, payloadJson, Decrypt(encryptedSecret), ct);

        if (failures.Count > 0)
        {
            logger.LogWarning("Webhook delivery failed for {FailCount}/{Total} URLs for instance {InstanceId}",
                failures.Count, urls.Length, payload.InstanceId);
            throw new InvalidOperationException($"Webhook delivery failed for {failures.Count} of {urls.Length} URLs");
        }

        logger.LogDebug("Webhook alert sent to {UrlCount} URLs for instance {InstanceId}",
            urls.Length, payload.InstanceId);
    }

    // A channel that carries a secret has a receiver that verifies signatures, so an
    // undecryptable secret fails the delivery rather than falling back to an unsigned POST the
    // receiver would reject without either side recording why.
    private string? Decrypt(string? encryptedSecret)
    {
        if (string.IsNullOrWhiteSpace(encryptedSecret))
        {
            return null;
        }

        try
        {
            return encryption.Decrypt(encryptedSecret);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw new InvalidOperationException("Webhook signing secret could not be decrypted", ex);
        }
    }
}
