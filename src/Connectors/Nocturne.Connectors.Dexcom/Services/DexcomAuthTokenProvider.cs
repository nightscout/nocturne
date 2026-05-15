using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.Dexcom.Services;

/// <summary>
///     Token provider for Dexcom Share authentication.
///     Handles the two-step authentication flow (authenticate → get session ID).
/// </summary>
public class DexcomAuthTokenProvider(
    HttpClient httpClient,
    IConnectorTokenCache tokenCache,
    IConnectorServerResolver<DexcomConnectorConfiguration> serverResolver,
    ITenantAccessor tenantAccessor,
    ILogger<DexcomAuthTokenProvider> logger,
    IRetryDelayStrategy retryDelayStrategy)
    : AuthTokenProviderBase<DexcomConnectorConfiguration>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
{
    private const string DexcomApplicationId = "d89443d2-327c-4a6f-89e5-496bbb0317db";

    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    /// <summary>
    ///     Dexcom sessions typically last 24 hours, but we refresh at 23 hours.
    /// </summary>
    protected override int TokenLifetimeBufferMinutes => 60;

    protected override string ConnectorName => "Dexcom";

    protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        DexcomConnectorConfiguration config, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;

        var sessionId = await ExecuteWithRetryAsync(
            async attempt =>
            {
                _logger.LogInformation(
                    "Authenticating with Dexcom Share for account: {Username} (attempt {Attempt}/{MaxRetries})",
                    config.Username,
                    attempt + 1,
                    maxRetries);

                var accountId = await AuthenticatePublisherAccountAsync(config, cancellationToken);
                if (string.IsNullOrEmpty(accountId))
                    return (null, true);

                var token = await LoginPublisherAccountAsync(config, accountId, cancellationToken);
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
            return (null, DateTime.MinValue, null);

        var expiresAt = DateTime.UtcNow.AddHours(24);
        _logger.LogInformation(
            "Dexcom Share authentication successful, session expires at {ExpiresAt}",
            expiresAt);

        return (sessionId, expiresAt, null);
    }

    private async Task<string?> AuthenticatePublisherAccountAsync(
        DexcomConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var authPayload = new
        {
            password = config.Password,
            applicationId = DexcomApplicationId,
            accountName = config.Username
        };

        var json = JsonSerializer.Serialize(authPayload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            _serverResolver.BuildUrl(config, "/ShareWebServices/Services/General/AuthenticatePublisherAccount"),
            content,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            await HandleErrorResponseAsync(response, "Dexcom authentication", cancellationToken);
            return null;
        }

        var accountId = await response.Content.ReadAsStringAsync(cancellationToken);
        accountId = accountId.Trim('"');

        if (!string.IsNullOrEmpty(accountId)) return accountId;
        _logger.LogError("Dexcom authentication returned empty account ID");
        return null;
    }

    private async Task<string?> LoginPublisherAccountAsync(
        DexcomConnectorConfiguration config, string accountId, CancellationToken cancellationToken)
    {
        var sessionPayload = new
        {
            password = config.Password,
            applicationId = DexcomApplicationId,
            accountId
        };

        var json = JsonSerializer.Serialize(sessionPayload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            _serverResolver.BuildUrl(config, "/ShareWebServices/Services/General/LoginPublisherAccountById"),
            content,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            await HandleErrorResponseAsync(response, "Dexcom session creation", cancellationToken);
            return null;
        }

        var sessionId = await response.Content.ReadAsStringAsync(cancellationToken);
        sessionId = sessionId.Trim('"');

        if (!string.IsNullOrEmpty(sessionId)) return sessionId;
        _logger.LogError("Dexcom session creation returned empty session ID");
        return null;
    }
}
