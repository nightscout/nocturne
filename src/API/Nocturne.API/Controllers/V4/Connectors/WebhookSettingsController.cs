using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Services.Alerts.Webhooks;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Controllers.V4.Connectors;

/// <summary>
/// Controller for managing outbound webhook notification settings (URL, headers, test dispatch).
/// </summary>
/// <remarks>
/// Webhook settings are persisted per-tenant and allow administrators to configure an external
/// HTTP endpoint that receives alert notifications. The <c>POST /test</c> endpoint sends a test
/// payload via <see cref="WebhookRequestSender"/> to verify connectivity before saving.
/// </remarks>
/// <seealso cref="WebhookRequestSender"/>
/// <seealso cref="WebhookNotificationSettings"/>
[ApiController]
[Tags("Connectors")]
[Route("api/v4/ui-settings/notifications/webhooks")]
public class WebhookSettingsController(
    WebhookRequestSender requestSender,
    ILogger<WebhookSettingsController> logger)
    : ControllerBase
{
    /// <summary>Gets the webhook notification settings for the current tenant.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(WebhookNotificationSettings), 200)]
    [ProducesResponseType(500)]
    public Task<ActionResult<WebhookNotificationSettings>> GetWebhookSettings(
        CancellationToken cancellationToken = default
    )
    {
        // TODO: Re-implement with new alert engine storage
        return Task.FromResult<ActionResult<WebhookNotificationSettings>>(Ok(
            new WebhookNotificationSettings
            {
                Enabled = false,
                Urls = new List<string>(),
                HasSecret = false,
                Secret = null,
                SignatureVersion = "v1",
            }
        ));
    }

    /// <summary>Saves webhook notification settings.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(WebhookNotificationSettings), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public Task<ActionResult<WebhookNotificationSettings>> SaveWebhookSettings(
        [FromBody] WebhookNotificationSettings settings,
        CancellationToken cancellationToken = default
    )
    {
        // TODO: Re-implement with new alert engine storage
        logger.LogWarning("Webhook settings save is a no-op until new alert engine is implemented");
        return Task.FromResult<ActionResult<WebhookNotificationSettings>>(Ok(settings));
    }

    /// <summary>Tests webhook settings by sending test payloads to configured URLs.</summary>
    [HttpPost("test")]
    [ProducesResponseType(typeof(WebhookTestResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<WebhookTestResult>> TestWebhookSettings(
        [FromBody] WebhookTestRequest request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var urls = request.Urls;

            if (urls == null || urls.Count == 0)
            {
                return Problem(detail: "Webhook URLs are required", statusCode: 400, title: "Bad Request");
            }

            var secret = request.Secret;
            if (string.IsNullOrWhiteSpace(secret))
            {
                return Problem(detail: "Webhook secret is required", statusCode: 400, title: "Bad Request");
            }

            var userId = GetUserId();
            var payload = JsonSerializer.Serialize(
                new
                {
                    kind = "webhook_test",
                    userId
                }
            );

            var failedUrls = await requestSender.SendAsync(
                urls,
                payload,
                secret,
                cancellationToken
            );

            return Ok(
                new WebhookTestResult
                {
                    Ok = failedUrls.Count == 0,
                    FailedUrls = failedUrls.ToArray(),
                }
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to test webhook settings");
            return Problem(detail: "Failed to test webhook settings", statusCode: 500, title: "Internal Server Error");
        }
    }

    private string GetUserId()
    {
        var userId =
            User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return string.IsNullOrEmpty(userId) ? "00000000-0000-0000-0000-000000000001" : userId;
    }
}

public sealed class WebhookTestRequest
{
    public List<string> Urls { get; set; } = [];
    public string? Secret { get; set; }
}

public sealed class WebhookTestResult
{
    public bool Ok { get; init; }
    public string[] FailedUrls { get; init; } = [];
}
