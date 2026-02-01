using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Services.Alerts.Webhooks;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Controllers.V4.Notifications;

[ApiController]
[Route("api/v4/ui-settings/notifications/webhooks")]
public class WebhookSettingsController(
    NotificationPreferencesRepository preferencesRepository,
    WebhookRequestSender requestSender,
    ILogger<WebhookSettingsController> logger)
    : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(WebhookNotificationSettings), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<WebhookNotificationSettings>> GetWebhookSettings(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var userId = GetUserId();
            var preferences = await preferencesRepository.GetPreferencesForUserAsync(
                userId,
                cancellationToken
            );

            var config = WebhookConfigurationParser.Parse(preferences?.WebhookUrls, logger);

            if (
                preferences is not null
                && config.Urls.Count > 0
                && string.IsNullOrWhiteSpace(config.Secret)
            )
            {
                var generatedSecret = WebhookSecretGenerator.Generate();
                preferences.WebhookUrls = WebhookConfigurationParser.Serialize(
                    config.Urls,
                    generatedSecret
                );
                await preferencesRepository.UpsertPreferencesAsync(preferences, cancellationToken);
                config = new WebhookConfiguration(config.Urls, generatedSecret);
            }

            return Ok(
                new WebhookNotificationSettings
                {
                    Enabled = preferences?.WebhookEnabled ?? false,
                    Urls = config.Urls.ToList(),
                    HasSecret = !string.IsNullOrWhiteSpace(config.Secret),
                    Secret = config.Secret,
                    SignatureVersion = "v1",
                }
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load webhook settings");
            return StatusCode(500, new { error = "Failed to load webhook settings" });
        }
    }

    [HttpPut]
    [ProducesResponseType(typeof(WebhookNotificationSettings), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<WebhookNotificationSettings>> SaveWebhookSettings(
        [FromBody] WebhookNotificationSettings settings,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var userId = GetUserId();
            var existing = await preferencesRepository.GetPreferencesForUserAsync(
                userId,
                cancellationToken
            );

            var existingConfig = WebhookConfigurationParser.Parse(existing?.WebhookUrls, logger);
            var secretToUse = string.IsNullOrWhiteSpace(settings.Secret)
                ? existingConfig.Secret
                : settings.Secret;

            if (settings.Enabled && string.IsNullOrWhiteSpace(secretToUse))
            {
                secretToUse = WebhookSecretGenerator.Generate();
            }

            var normalizedPayload = WebhookConfigurationParser.Serialize(
                settings.Urls,
                secretToUse
            );
            var normalizedConfig = WebhookConfigurationParser.Parse(normalizedPayload, logger);

            var preferences = existing ?? new NotificationPreferencesEntity { UserId = userId };
            preferences.WebhookEnabled = settings.Enabled && normalizedConfig.Urls.Count > 0;
            preferences.WebhookUrls = normalizedPayload;

            await preferencesRepository.UpsertPreferencesAsync(preferences, cancellationToken);

            return Ok(
                new WebhookNotificationSettings
                {
                    Enabled = preferences.WebhookEnabled,
                    Urls = normalizedConfig.Urls.ToList(),
                    HasSecret = !string.IsNullOrWhiteSpace(secretToUse),
                    Secret = secretToUse,
                    SignatureVersion = "v1",
                }
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save webhook settings");
            return StatusCode(500, new { error = "Failed to save webhook settings" });
        }
    }

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
            var userId = GetUserId();
            var preferences = await preferencesRepository.GetPreferencesForUserAsync(
                userId,
                cancellationToken
            );
            var config = WebhookConfigurationParser.Parse(preferences?.WebhookUrls, logger);
            var urls = request.Urls?.Count > 0 ? request.Urls : config.Urls;

            if (urls.Count == 0)
            {
                return BadRequest(new { error = "Webhook URLs are required" });
            }

            var secret = string.IsNullOrWhiteSpace(request.Secret) ? config.Secret : request.Secret;
            if (string.IsNullOrWhiteSpace(secret))
            {
                return BadRequest(new { error = "Webhook secret is required" });
            }

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
            return StatusCode(500, new { error = "Failed to test webhook settings" });
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
