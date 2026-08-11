using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Notifications;

/// <summary>
/// Sends push notifications to the Pushover API and polls for receipt acknowledgements.
/// Implements full 1:1 legacy compatibility with the original Nightscout Pushover integration,
/// including emergency-priority receipt tracking via <see cref="INotificationV1Service"/>.
/// </summary>
/// <seealso cref="IPushoverService"/>
public class PushoverService : IPushoverService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PushoverService> _logger;
    private readonly INotificationV1Service _notificationService;
    private readonly IConfiguration _configuration;
    private readonly BaseDomainOptions _baseDomain;

    private const string PUSHOVER_API_URL = "https://api.pushover.net/1/messages.json";

    public PushoverService(
        HttpClient httpClient,
        ILogger<PushoverService> logger,
        INotificationV1Service notificationService,
        IConfiguration configuration,
        IOptions<BaseDomainOptions> baseDomainOptions
    )
    {
        _httpClient = httpClient;
        _logger = logger;
        _notificationService = notificationService;
        _configuration = configuration;
        _baseDomain = baseDomainOptions.Value;
    }

    /// <summary>
    /// Sends a Pushover notification for an alarm
    /// Maintains 1:1 compatibility with legacy Pushover notification sending
    /// </summary>
    /// <param name="request">Pushover notification request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Pushover response with receipt information</returns>
    public async Task<PushoverResponse> SendNotificationAsync(
        PushoverNotificationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug(
                "Sending Pushover notification - title: {Title}, priority: {Priority}",
                request.Title,
                request.Priority
            );

            // Get API token from configuration
            var apiToken =
                _configuration[ServiceNames.ConfigKeys.PushoverApiToken]
                ?? _configuration[ServiceNames.ConfigKeys.PushoverApiTokenEnv];
            if (string.IsNullOrEmpty(apiToken))
            {
                _logger.LogWarning("Pushover API token not configured");
                return new PushoverResponse
                {
                    Success = false,
                    Error = "Pushover API token not configured",
                };
            }

            // Get user key from configuration
            var userKey =
                _configuration[ServiceNames.ConfigKeys.PushoverUserKey]
                ?? _configuration[ServiceNames.ConfigKeys.PushoverUserKeyEnv];
            if (string.IsNullOrEmpty(userKey))
            {
                _logger.LogWarning("Pushover user key not configured");
                return new PushoverResponse
                {
                    Success = false,
                    Error = "Pushover user key not configured",
                };
            }

            // Build callback URL for receipt acknowledgment if priority is 2 (emergency).
            // Requires a public origin — Pushover's servers must be able to reach it.
            string? callbackUrl = null;
            if (request.Priority == 2 && _baseDomain.PublicOrigin is { } origin)
            {
                callbackUrl = $"{origin}/api/v1/notifications/pushovercallback";
            }

            // Build form data for Pushover API
            var formData = new List<KeyValuePair<string, string>>
            {
                new("token", apiToken),
                new("user", userKey),
                new("title", request.Title),
                new("message", request.Message),
            };

            // Add optional parameters based on legacy behavior
            if (request.Priority.HasValue)
            {
                formData.Add(new("priority", request.Priority.Value.ToString()));

                // Emergency priority requires retry and expire parameters
                if (request.Priority.Value == 2)
                {
                    formData.Add(new("retry", (request.Retry ?? 60).ToString())); // Default 60 seconds
                    formData.Add(new("expire", (request.Expire ?? 3600).ToString())); // Default 1 hour

                    if (!string.IsNullOrEmpty(callbackUrl))
                    {
                        formData.Add(new("callback", callbackUrl));
                    }
                }
            }

            if (!string.IsNullOrEmpty(request.Sound))
            {
                formData.Add(new("sound", request.Sound));
            }

            if (!string.IsNullOrEmpty(request.Device))
            {
                formData.Add(new("device", request.Device));
            }

            if (!string.IsNullOrEmpty(request.Url))
            {
                formData.Add(new("url", request.Url));
            }

            if (!string.IsNullOrEmpty(request.UrlTitle))
            {
                formData.Add(new("url_title", request.UrlTitle));
            }

            // Send request to Pushover API
            using var content = new FormUrlEncodedContent(formData);
            var response = await _httpClient.PostAsync(
                PUSHOVER_API_URL,
                content,
                cancellationToken
            );
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogDebug(
                "Pushover API response: {StatusCode}, Content: {Content}",
                response.StatusCode,
                responseContent
            );

            // Parse response
            var pushoverResponse = JsonSerializer.Deserialize<PushoverApiResponse>(
                responseContent,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            if (pushoverResponse?.Status == 1)
            {
                // Successfully sent, register receipt mapping if available
                if (!string.IsNullOrEmpty(pushoverResponse.Receipt) && request.Level.HasValue)
                {
                    _notificationService.RegisterPushoverReceipt(
                        pushoverResponse.Receipt,
                        request.Level.Value,
                        request.Group ?? "default",
                        request.Title,
                        request.Message
                    );

                    _logger.LogInformation(
                        "Pushover notification sent successfully - receipt: {Receipt}, level: {Level}, group: {Group}",
                        pushoverResponse.Receipt,
                        request.Level.Value,
                        request.Group ?? "default"
                    );
                }
                else
                {
                    _logger.LogInformation(
                        "Pushover notification sent successfully - no receipt tracking needed"
                    );
                }

                return new PushoverResponse
                {
                    Success = true,
                    Receipt = pushoverResponse.Receipt,
                    Request = pushoverResponse.Request,
                };
            }
            else
            {
                _logger.LogWarning(
                    "Pushover API returned error: {Errors}",
                    string.Join(
                        ", ",
                        pushoverResponse?.Errors ?? new List<string> { "Unknown error" }
                    )
                );

                return new PushoverResponse
                {
                    Success = false,
                    Error = string.Join(
                        ", ",
                        pushoverResponse?.Errors ?? new List<string> { "Unknown error" }
                    ),
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending Pushover notification");
            return new PushoverResponse
            {
                Success = false,
                Error = "Internal error sending Pushover notification",
            };
        }
    }

    /// <summary>
    /// Creates a Pushover notification request from alarm details
    /// Implements legacy alarm-to-Pushover mapping logic
    /// </summary>
    /// <param name="level">Alarm level (1=WARN, 2=URGENT)</param>
    /// <param name="group">Alarm group</param>
    /// <param name="title">Notification title</param>
    /// <param name="message">Notification message</param>
    /// <param name="sound">Pushover sound (optional)</param>
    /// <returns>Pushover notification request</returns>
    public PushoverNotificationRequest CreateAlarmNotification(
        int level,
        string group,
        string title,
        string message,
        string? sound = null
    )
    {
        var request = new PushoverNotificationRequest
        {
            Level = level,
            Group = group,
            Title = title,
            Message = message,
            Sound = sound,
        };

        // Set priority based on alarm level (legacy behavior)
        switch (level)
        {
            case 1: // WARN
                request.Priority = 0; // Normal priority
                break;
            case 2: // URGENT
                request.Priority = 2; // Emergency priority (requires acknowledgment)
                request.Retry = 60; // Retry every 60 seconds
                request.Expire = 3600; // Expire after 1 hour
                break;
            default:
                request.Priority = -1; // Low priority
                break;
        }

        // Use appropriate sound if not specified
        if (string.IsNullOrEmpty(sound))
        {
            request.Sound = level == 2 ? "persistent" : "default";
        }

        return request;
    }

    /// <summary>
    /// Internal Pushover API response model
    /// </summary>
    private class PushoverApiResponse
    {
        public int Status { get; set; }
        public string? Request { get; set; }
        public string? Receipt { get; set; }
        public List<string>? Errors { get; set; }
    }
}
