using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Configurations;

namespace Nocturne.Connectors.Dexcom.Services;

/// <summary>
///     Token provider for Dexcom Share authentication.
///     Handles the two-step authentication flow (authenticate → get session ID).
/// </summary>
public class DexcomAuthTokenProvider(
    IOptions<DexcomConnectorConfiguration> config,
    HttpClient httpClient,
    ILogger<DexcomAuthTokenProvider> logger,
    IRetryDelayStrategy retryDelayStrategy)
    : AuthTokenProviderBase<DexcomConnectorConfiguration>(config.Value, httpClient, logger)
{
    private const string DexcomApplicationId = "d89443d2-327c-4a6f-89e5-496bbb0317db";

    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    /// <summary>
    ///     Dexcom sessions typically last 24 hours, but we refresh at 23 hours.
    /// </summary>
    protected override int TokenLifetimeBufferMinutes => 60;

    protected override async Task<(string? Token, DateTime ExpiresAt)> AcquireTokenAsync(
        CancellationToken cancellationToken)
    {
        const int maxRetries = 3;

        var sessionId = await ExecuteWithRetryAsync(
            async attempt =>
            {
                _logger.LogInformation(
                    "Authenticating with Dexcom Share for account: {Username} (attempt {Attempt}/{MaxRetries})",
                    _config.Username,
                    attempt + 1,
                    maxRetries);

                var accountId = await AuthenticatePublisherAccountAsync(cancellationToken);
                if (string.IsNullOrEmpty(accountId))
                    return (null, true);

                var token = await LoginPublisherAccountAsync(accountId, cancellationToken);
                if (string.IsNullOrEmpty(token))
                    return (null, true);

                return (token, false);
            },
            _retryDelayStrategy,
            maxRetries,
            "Dexcom authentication",
            cancellationToken
        );

        if (string.IsNullOrEmpty(sessionId))
            return (null, DateTime.MinValue);

        var expiresAt = DateTime.UtcNow.AddHours(24);
        _logger.LogInformation(
            "Dexcom Share authentication successful, session expires at {ExpiresAt}",
            expiresAt);

        return (sessionId, expiresAt);
    }

    private async Task<string?> AuthenticatePublisherAccountAsync(CancellationToken cancellationToken)
    {
        var authPayload = new
        {
            password = _config.Password,
            applicationId = DexcomApplicationId,
            accountName = _config.Username
        };

        var json = JsonSerializer.Serialize(authPayload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            "/ShareWebServices/Services/General/AuthenticatePublisherAccount",
            content,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsRetryableError())
            {
                _logger.LogWarning(
                    "Dexcom authentication failed with retryable error: {StatusCode} - {Error}",
                    response.StatusCode,
                    errorContent);
                return null;
            }

            _logger.LogError(
                "Dexcom authentication failed with non-retryable error: {StatusCode} - {Error}",
                response.StatusCode,
                errorContent);
            return null;
        }

        var accountId = await response.Content.ReadAsStringAsync(cancellationToken);
        accountId = accountId.Trim('"');

        if (!string.IsNullOrEmpty(accountId)) return accountId;
        _logger.LogError("Dexcom authentication returned empty account ID");
        return null;
    }

    private async Task<string?> LoginPublisherAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        var sessionPayload = new
        {
            password = _config.Password,
            applicationId = DexcomApplicationId,
            accountId
        };

        var json = JsonSerializer.Serialize(sessionPayload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            "/ShareWebServices/Services/General/LoginPublisherAccountById",
            content,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsRetryableError())
            {
                _logger.LogWarning(
                    "Dexcom session creation failed with retryable error: {StatusCode} - {Error}",
                    response.StatusCode,
                    errorContent);
                return null;
            }

            _logger.LogError(
                "Dexcom session creation failed with non-retryable error: {StatusCode} - {Error}",
                response.StatusCode,
                errorContent);
            return null;
        }

        var sessionId = await response.Content.ReadAsStringAsync(cancellationToken);
        sessionId = sessionId.Trim('"');

        if (!string.IsNullOrEmpty(sessionId)) return sessionId;
        _logger.LogError("Dexcom session creation returned empty session ID");
        return null;
    }
}