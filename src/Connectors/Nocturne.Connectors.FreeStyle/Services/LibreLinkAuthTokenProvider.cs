using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.FreeStyle.Configurations.Constants;

namespace Nocturne.Connectors.FreeStyle.Services;

/// <summary>
///     Token provider for LibreLinkUp authentication.
///     Returns a Bearer token for API requests.
/// </summary>
public class LibreLinkAuthTokenProvider(
    IOptions<LibreLinkUpConnectorConfiguration> config,
    HttpClient httpClient,
    ILogger<LibreLinkAuthTokenProvider> logger,
    IRetryDelayStrategy retryDelayStrategy
) : AuthTokenProviderBase<LibreLinkUpConnectorConfiguration>(config.Value, httpClient, logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy
        ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    /// <summary>
    ///     LibreLinkUp tokens typically last 24 hours.
    /// </summary>
    protected override int TokenLifetimeBufferMinutes => 60;

    protected override async Task<(string? Token, DateTime ExpiresAt)> AcquireTokenAsync(
        CancellationToken cancellationToken)
    {
        const int maxRetries = LibreLinkUpConstants.Configuration.MaxRetries;

        var token = await ExecuteWithRetryAsync(
            async attempt =>
            {
                _logger.LogInformation(
                    "Authenticating with LibreLinkUp for user: {Username} (attempt {Attempt}/{MaxRetries})",
                    _config.LibreUsername,
                    attempt + 1,
                    maxRetries);

                var loginPayload = new
                {
                    email = _config.LibreUsername,
                    password = _config.LibrePassword
                };

                var json = JsonSerializer.Serialize(loginPayload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(
                    LibreLinkUpConstants.ApiPaths.Login,
                    content,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

                    if (response.IsRetryableError())
                    {
                        _logger.LogWarning(
                            "LibreLinkUp authentication failed with retryable error on attempt {Attempt}: {StatusCode} - {Error}",
                            attempt + 1,
                            response.StatusCode,
                            errorContent);
                        return (null, true);
                    }

                    _logger.LogError(
                        "LibreLinkUp authentication failed with non-retryable error: {StatusCode} - {Error}",
                        response.StatusCode,
                        errorContent);
                    return (null, false);
                }

                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var loginResponse = JsonSerializer.Deserialize<LibreLoginResponse>(
                    responseContent,
                    JsonOptions
                );

                if (loginResponse?.Data?.AuthTicket?.Token == null)
                {
                    _logger.LogError("LibreLinkUp authentication failed: Invalid response structure");
                    return (null, false);
                }

                return (loginResponse.Data.AuthTicket.Token, false);
            },
            _retryDelayStrategy,
            maxRetries,
            "LibreLinkUp authentication",
            cancellationToken
        );

        if (string.IsNullOrEmpty(token))
            return (null, DateTime.MinValue);

        var expiresAt = DateTime.UtcNow.AddHours(24);

        _logger.LogInformation(
            "LibreLinkUp authentication successful, token expires at {ExpiresAt}",
            expiresAt);

        return (token, expiresAt);
    }

    #region Response Models

    private class LibreLoginResponse
    {
        public LibreLoginData? Data { get; set; }
    }

    private class LibreLoginData
    {
        public LibreAuthTicket? AuthTicket { get; set; }
    }

    private class LibreAuthTicket
    {
        public string? Token { get; set; }
    }

    #endregion
}
