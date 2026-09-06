using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Connectors.Glooko.Utilities;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.Glooko.Services;

/// <summary>
///     Token provider for Glooko authentication.
///     Handles session cookie extraction for API requests.
///     Note: Glooko returns a session cookie rather than a bearer token,
///     but we represent it as a token for consistency.
/// </summary>
public class GlookoAuthTokenProvider(
    HttpClient httpClient,
    IConnectorTokenCache tokenCache,
    IConnectorServerResolver<GlookoConnectorConfiguration> serverResolver,
    ITenantAccessor tenantAccessor,
    ILogger<GlookoAuthTokenProvider> logger,
    IRetryDelayStrategy retryDelayStrategy)
    : AuthTokenProviderBase<GlookoConnectorConfiguration>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
{
    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    protected override string ConnectorName => "Glooko";

    protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        GlookoConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var maxRetries = LoginAttempts(config);
        IReadOnlyDictionary<string, string>? metadata = null;

        var sessionCookie = await ExecuteWithRetryAsync<string>(
            async attempt =>
            {
                _logger.LogInformation(
                    "Authenticating with Glooko server: {Server} (v3={UseV3}, attempt {Attempt}/{MaxRetries})",
                    config.Server, config.UseV3Api, attempt + 1, maxRetries);

                var (cookie, sessionMetadata, shouldRetry) = await SignInAsync(config, cancellationToken);
                if (cookie == null)
                    return (null, shouldRetry);

                metadata = sessionMetadata;
                return (cookie, false);
            },
            _retryDelayStrategy,
            maxRetries,
            "Glooko authentication",
            cancellationToken);

        if (string.IsNullOrEmpty(sessionCookie))
            return (null, DateTime.MinValue, null);

        _logger.LogInformation("Glooko authentication successful");
        return (sessionCookie, DateTime.UtcNow.Add(GlookoConstants.SessionLifetime), metadata);
    }

    /// <summary>
    ///     Performs one sign-in attempt, returning the session cookie and the metadata cached with it,
    ///     or null plus whether the failure is worth another attempt.
    /// </summary>
    private async Task<(string? SessionCookie, IReadOnlyDictionary<string, string>? Metadata, bool ShouldRetry)> SignInAsync(
        GlookoConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var baseUrl = GlookoConstants.ResolveBaseUrl(config.Server);
        var webOrigin = GlookoConstants.ResolveWebOrigin(config.Server);

        string signInPath;
        string loginJson;

        if (config.UseV3Api)
        {
            signInPath = GlookoConstants.V3SignInPath;
            var loginData = new
            {
                user = new
                {
                    email = config.Email,
                    password = config.Password
                }
            };
            loginJson = JsonSerializer.Serialize(loginData);
        }
        else
        {
            signInPath = GlookoConstants.SignInPath;
            var loginData = new
            {
                userLogin = new
                {
                    email = config.Email,
                    password = config.Password
                },
                deviceInformation = GlookoConstants.DeviceInformation
            };
            loginJson = JsonSerializer.Serialize(loginData);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{signInPath}")
        {
            Content = new StringContent(loginJson, Encoding.UTF8, "application/json")
        };

        GlookoHttpHelper.ApplyStandardHeaders(request, webOrigin);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Read through the Glooko helper rather than the base class's reader: Glooko returns
            // gzip bodies the HTTP layer has not decompressed, so the raw bytes are unreadable.
            var errorContent = await GlookoHttpHelper.ReadResponseAsync(response, cancellationToken);
            var shouldRetry = response.IsRetryableError();
            if (shouldRetry)
                _logger.LogWarning("Glooko authentication failed with retryable error: {StatusCode} - {Error}",
                    response.StatusCode, errorContent);
            else
                _logger.LogError("Glooko authentication failed with non-retryable error: {StatusCode} - {Error}",
                    response.StatusCode, errorContent);

            return (null, null, shouldRetry);
        }

        // Extract session cookie from response headers
        string? sessionCookie = null;
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            foreach (var cookie in cookies)
                if (cookie.StartsWith($"{GlookoConstants.SessionCookieName}="))
                {
                    sessionCookie = cookie.Split(';')[0];
                    _logger.LogInformation("Session cookie extracted successfully");
                    break;
                }

        // Parse user data from sign-in response (V2 only — V3 sign-in returns { success, two_fa_required })
        var responseJson = await GlookoHttpHelper.ReadResponseAsync(response, cancellationToken);
        GlookoUserData? userData = null;
        if (!config.UseV3Api)
        {
            try
            {
                userData = JsonSerializer.Deserialize<GlookoUserData>(responseJson);
                if (userData?.GlookoCode != null)
                    _logger.LogInformation(
                        "User data parsed successfully. Glooko code: {GlookoCode}",
                        userData.GlookoCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not parse user data: {Message}", ex.Message);
            }
        }

        if (string.IsNullOrEmpty(sessionCookie))
        {
            // A success status with no session cookie is Glooko declining the sign-in in the body
            // rather than in the status line (V3 answers { success, two_fa_required }); the same
            // request cannot produce a cookie on a second attempt.
            _logger.LogError("Failed to extract session cookie from Glooko response");
            return (null, null, false);
        }

        // V3 sign-in doesn't return user data — fetch it from /api/v3/session/users
        if (config.UseV3Api)
        {
            try
            {
                var v3User = await FetchV3UserDataAsync(baseUrl, webOrigin, sessionCookie, cancellationToken);
                if (v3User != null)
                {
                    userData = new GlookoUserData { User = new GlookoUserLogin { GlookoCode = v3User.GlookoCode } };
                    _logger.LogInformation(
                        "V3 user profile loaded. Glooko code: {GlookoCode}, MeterUnits: {Units}",
                        v3User.GlookoCode, v3User.MeterUnits);
                }
                else
                {
                    _logger.LogWarning("V3 sign-in succeeded but failed to fetch user profile");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch V3 user profile after sign-in");
            }
        }

        var metadata = new Dictionary<string, string> { ["SessionCookie"] = sessionCookie };
        if (userData != null)
            metadata["UserData"] = JsonSerializer.Serialize(userData);

        return (sessionCookie, metadata, false);
    }

    /// <summary>
    ///     Fetches the user profile from /api/v3/session/users after V3 sign-in.
    ///     The V3 sign-in response only contains { success, two_fa_required } —
    ///     the glookoCode and meter units come from this follow-up call.
    /// </summary>
    private async Task<GlookoV3User?> FetchV3UserDataAsync(
        string baseUrl, string webOrigin, string sessionCookie, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}{GlookoConstants.V3UsersPath}");
        GlookoHttpHelper.ApplyStandardHeaders(request, webOrigin, sessionCookie);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch V3 user profile: {StatusCode}", response.StatusCode);
            return null;
        }

        var json = await GlookoHttpHelper.ReadResponseAsync(response, cancellationToken);
        var profile = JsonSerializer.Deserialize<GlookoV3UsersResponse>(json);

        return profile?.CurrentUser ?? profile?.CurrentPatient;
    }
}

/// <summary>
///     Glooko user data returned from authentication.
///     V2 returns { userLogin: { glookoCode } }, V3 returns { user: { glookoCode } }.
///     Both shapes are deserialized into this single model.
/// </summary>
public class GlookoUserData
{
    [JsonPropertyName("userLogin")] public GlookoUserLogin? UserLogin { get; set; }

    [JsonPropertyName("user")] public GlookoUserLogin? User { get; set; }

    /// <summary>
    ///     Gets the Glooko code from whichever response shape was returned.
    /// </summary>
    public string? GlookoCode => UserLogin?.GlookoCode ?? User?.GlookoCode;
}

/// <summary>
///     Glooko user login details.
/// </summary>
public class GlookoUserLogin
{
    [JsonPropertyName("glookoCode")] public string? GlookoCode { get; set; }
}
