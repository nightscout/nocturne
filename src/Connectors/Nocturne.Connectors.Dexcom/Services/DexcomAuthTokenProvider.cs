using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;

namespace Nocturne.Connectors.Dexcom.Services;

/// <summary>
/// Token provider for Dexcom Share authentication.
/// Handles the two-step authentication flow (authenticate → get session ID).
/// </summary>
public class DexcomAuthTokenProvider : AuthTokenProviderBase<DexcomConnectorConfiguration>
{
    private const string DexcomApplicationId = "d89443d2-327c-4a6f-89e5-496bbb0317db";
    private readonly IRetryDelayStrategy _retryDelayStrategy;

    public DexcomAuthTokenProvider(
        IOptions<DexcomConnectorConfiguration> config,
        HttpClient httpClient,
        ILogger<DexcomAuthTokenProvider> logger,
        IRetryDelayStrategy retryDelayStrategy)
        : base(config.Value, httpClient, logger)
    {
        _retryDelayStrategy = retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
    }

    /// <summary>
    /// Dexcom sessions typically last 24 hours, but we refresh at 23 hours.
    /// </summary>
    protected override int TokenLifetimeBufferMinutes => 60;

    protected override async Task<(string? Token, DateTime ExpiresAt)> AcquireTokenAsync(CancellationToken cancellationToken)
    {
        const int maxRetries = 3;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "Authenticating with Dexcom Share for account: {Username} (attempt {Attempt}/{MaxRetries})",
                    _config.DexcomUsername,
                    attempt + 1,
                    maxRetries);

                // Step 1: Authenticate and get account ID
                var accountId = await AuthenticatePublisherAccountAsync(cancellationToken);
                if (string.IsNullOrEmpty(accountId))
                {
                    if (attempt < maxRetries - 1)
                    {
                        await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
                        continue;
                    }
                    return (null, DateTime.MinValue);
                }

                // Step 2: Get session ID using the account ID
                var sessionId = await LoginPublisherAccountAsync(accountId, cancellationToken);
                if (string.IsNullOrEmpty(sessionId))
                {
                    if (attempt < maxRetries - 1)
                    {
                        await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
                        continue;
                    }
                    return (null, DateTime.MinValue);
                }

                // Session acquired successfully
                var expiresAt = DateTime.UtcNow.AddHours(24);
                _logger.LogInformation(
                    "Dexcom Share authentication successful, session expires at {ExpiresAt}",
                    expiresAt);

                return (sessionId, expiresAt);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    ex,
                    "HTTP error during Dexcom authentication attempt {Attempt}: {Message}",
                    attempt + 1,
                    ex.Message);

                if (attempt < maxRetries - 1)
                {
                    await _retryDelayStrategy.ApplyRetryDelayAsync(attempt);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error during Dexcom authentication attempt {Attempt}",
                    attempt + 1);
                return (null, DateTime.MinValue);
            }
        }

        _logger.LogError("Dexcom authentication failed after {MaxRetries} attempts", maxRetries);
        return (null, DateTime.MinValue);
    }

    private async Task<string?> AuthenticatePublisherAccountAsync(CancellationToken cancellationToken)
    {
        var authPayload = new
        {
            password = _config.DexcomPassword,
            applicationId = DexcomApplicationId,
            accountName = _config.DexcomUsername,
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

        if (string.IsNullOrEmpty(accountId))
        {
            _logger.LogError("Dexcom authentication returned empty account ID");
            return null;
        }

        return accountId;
    }

    private async Task<string?> LoginPublisherAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        var sessionPayload = new
        {
            password = _config.DexcomPassword,
            applicationId = DexcomApplicationId,
            accountId = accountId,
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

        if (string.IsNullOrEmpty(sessionId))
        {
            _logger.LogError("Dexcom session creation returned empty session ID");
            return null;
        }

        return sessionId;
    }
}
