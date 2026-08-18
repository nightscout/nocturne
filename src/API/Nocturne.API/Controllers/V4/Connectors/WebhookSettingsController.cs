using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts.Webhooks;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Controllers.V4.Connectors;

/// <summary>
/// Controller for testing outbound webhook delivery.
/// </summary>
/// <remarks>
/// The <c>POST /test</c> endpoint sends a test payload via <see cref="WebhookRequestSender"/>.
/// The GET/PUT pair has no storage behind it — see <see cref="NoStorageDetail"/>.
/// </remarks>
/// <seealso cref="WebhookRequestSender"/>
/// <seealso cref="WebhookNotificationSettings"/>
[ApiController]
[Tags("Connectors")]
[Route("api/v4/ui-settings/notifications/webhooks")]
[Authorize]
// Choosing where a tenant's alerts are delivered, and dispatching from the server to a
// caller-named host, are tenant administration. The GET is left on [Authorize] alone: it exposes
// no tenant state.
public class WebhookSettingsController(
    WebhookRequestSender requestSender,
    ILogger<WebhookSettingsController> logger)
    : ControllerBase
{
    /// <summary>
    /// Why the GET/PUT pair reports 501 instead of persisting. The alert engine addresses
    /// webhooks per rule — an <c>alert_rule_channels</c> row with
    /// <see cref="Core.Models.Alerts.ChannelType.Webhook"/> whose destination holds the URLs and
    /// whose secret signs them — so there is no tenant-wide record to read or write. A 200 here
    /// reported a save that never happened, on the alerting path.
    /// </summary>
    private const string NoStorageDetail =
        "Tenant-wide webhook settings are not stored. Webhook destinations belong to individual "
        + "alert rules: attach a webhook channel to a rule via /api/v4/alert-rules instead.";

    /// <summary>Gets the webhook notification settings for the current tenant.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(WebhookNotificationSettings), 200)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status501NotImplemented)]
    public ActionResult<WebhookNotificationSettings> GetWebhookSettings() => NotStored();

    /// <summary>Saves webhook notification settings.</summary>
    [HttpPut]
    [RequireScope(TenantPermissions.TenantSettings)]
    [ProducesResponseType(typeof(WebhookNotificationSettings), 200)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status501NotImplemented)]
    public ActionResult<WebhookNotificationSettings> SaveWebhookSettings(
        [FromBody] WebhookNotificationSettings settings
    ) => NotStored();

    private ObjectResult NotStored() => Problem(
        detail: NoStorageDetail,
        statusCode: StatusCodes.Status501NotImplemented,
        title: "Not Implemented"
    );

    /// <summary>Tests webhook settings by sending test payloads to configured URLs.</summary>
    /// <remarks>
    /// Gated for the demo's shared visitor because the destination is caller-chosen: the server
    /// makes an outbound POST from its own address. <c>OutboundDestination</c> keeps it off private
    /// networks, so what remains is a relay to public hosts.
    /// </remarks>
    [HttpPost("test")]
    [DenyDemoSubject]
    [RequireScope(TenantPermissions.TenantSettings)]
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

            // The test payload attributes the dispatch to the caller. A subject-less caller (the
            // instance key, a guest session) is reported as null rather than as a stand-in
            // identity the receiving endpoint could mistake for a real subject.
            var userId = HttpContext.GetSubjectIdString();
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
