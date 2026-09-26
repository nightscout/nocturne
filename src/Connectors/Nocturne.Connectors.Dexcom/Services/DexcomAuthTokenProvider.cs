using System.Net;
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

    protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        DexcomConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var maxRetries = LoginAttempts(config);

        var sessionId = await ExecuteWithRetryAsync(
            async attempt =>
            {
                _logger.LogInformation(
                    "Authenticating with Dexcom Share for account: {Username} (attempt {Attempt}/{MaxRetries})",
                    config.Username,
                    attempt + 1,
                    maxRetries);

                var (accountId, retryAuthentication) =
                    await AuthenticatePublisherAccountAsync(config, cancellationToken);
                if (string.IsNullOrEmpty(accountId))
                    return (null, retryAuthentication);

                var (token, retryLogin) =
                    await LoginPublisherAccountAsync(config, accountId, cancellationToken);
                if (string.IsNullOrEmpty(token))
                    return (null, retryLogin);

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

    /// <summary>
    ///     Resolves the publisher account id, or null plus whether the failure is worth another
    ///     attempt. An empty id on a 2xx is Dexcom's answer for this account, not a transient fault.
    /// </summary>
    private async Task<(string? AccountId, bool ShouldRetry)> AuthenticatePublisherAccountAsync(
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
            return (null, await ShouldRetryFailureAsync(response, "Dexcom authentication", cancellationToken));

        var accountId = await response.Content.ReadAsStringAsync(cancellationToken);
        accountId = accountId.Trim('"');

        if (!string.IsNullOrEmpty(accountId)) return (accountId, false);
        _logger.LogError("Dexcom authentication returned empty account ID");
        return (null, false);
    }

    /// <inheritdoc cref="AuthenticatePublisherAccountAsync"/>
    private async Task<(string? SessionId, bool ShouldRetry)> LoginPublisherAccountAsync(
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
            return (null, await ShouldRetryFailureAsync(response, "Dexcom session creation", cancellationToken));

        var sessionId = await response.Content.ReadAsStringAsync(cancellationToken);
        sessionId = sessionId.Trim('"');

        if (!string.IsNullOrEmpty(sessionId)) return (sessionId, false);
        _logger.LogError("Dexcom session creation returned empty session ID");
        return (null, false);
    }

    /// <summary>
    ///     Classifies a failed login response. The body is read before the status because the status
    ///     is the part Dexcom gets wrong: a refused credential arrives as a 500, and a status Dexcom
    ///     does get right would otherwise short-circuit the codes in
    ///     <see cref="DexcomConstants.RejectedCredentialCodes"/> before they are looked at. A refusal
    ///     leaves as the status it stands for, so the shared retry loop classifies it like any other.
    /// </summary>
    private async Task<bool> ShouldRetryFailureAsync(
        HttpResponseMessage response, string operationName, CancellationToken cancellationToken)
    {
        var code = ReadErrorCode(await response.Content.ReadAsStringAsync(cancellationToken));
        if (code != null && DexcomConstants.RejectedCredentialCodes.Contains(code))
            throw new HttpRequestException(
                $"{operationName} was refused by Dexcom: {code}", null, HttpStatusCode.Unauthorized);

        return await HandleErrorResponseAsync(response, operationName, cancellationToken);
    }

    private static string? ReadErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("Code", out var code)
                   && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
