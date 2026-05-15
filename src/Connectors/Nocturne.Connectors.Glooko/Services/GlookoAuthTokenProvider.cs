using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
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
public class GlookoAuthTokenProvider : AuthTokenProviderBase<GlookoConnectorConfiguration>
{
    public GlookoAuthTokenProvider(
        HttpClient httpClient,
        IConnectorTokenCache tokenCache,
        IConnectorServerResolver<GlookoConnectorConfiguration> serverResolver,
        ITenantAccessor tenantAccessor,
        ILogger<GlookoAuthTokenProvider> logger)
        : base(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
    {
    }

    protected override string ConnectorName => "Glooko";

    protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        GlookoConnectorConfiguration config, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Authenticating with Glooko server: {Server} (v3={UseV3})",
                config.Server, config.UseV3Api);

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

            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{signInPath}")
            {
                Content = new StringContent(loginJson, Encoding.UTF8, "application/json")
            };

            GlookoHttpHelper.ApplyStandardHeaders(request, webOrigin);

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await GlookoHttpHelper.ReadResponseAsync(response, cancellationToken);
                _logger.LogError("Glooko authentication failed: {StatusCode} - {Error}",
                    response.StatusCode, errorContent);
                return (null, DateTime.MinValue, null);
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

            if (!string.IsNullOrEmpty(sessionCookie))
            {
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

                _logger.LogInformation("Glooko authentication successful");

                var metadata = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(sessionCookie))
                    metadata["SessionCookie"] = sessionCookie;
                if (userData != null)
                {
                    var userDataJson = JsonSerializer.Serialize(userData);
                    metadata["UserData"] = userDataJson;
                }

                return (sessionCookie, DateTime.UtcNow.Add(GlookoConstants.SessionLifetime), metadata);
            }

            _logger.LogError("Failed to extract session cookie from Glooko response");
            return (null, DateTime.MinValue, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Glooko authentication error: {Message}", ex.Message);
            return (null, DateTime.MinValue, null);
        }
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
