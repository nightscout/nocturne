using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Connectors.Glooko.Utilities;

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
        IOptions<GlookoConnectorConfiguration> config,
        HttpClient httpClient,
        ILogger<GlookoAuthTokenProvider> logger)
        : base(config.Value, httpClient, logger)
    {
    }

    /// <summary>
    ///     Gets the user data obtained during authentication.
    ///     Contains the Glooko code needed for API requests.
    /// </summary>
    public GlookoUserData? UserData { get; private set; }

    /// <summary>
    ///     Gets the session cookie for API requests.
    /// </summary>
    public string? SessionCookie { get; private set; }

    protected override async Task<(string? Token, DateTime ExpiresAt)> AcquireTokenAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Authenticating with Glooko server: {Server} (v3={UseV3})",
                _config.Server, _config.UseV3Api);

            var baseUrl = GlookoConstants.ResolveBaseUrl(_config.Server);
            var webOrigin = GlookoConstants.ResolveWebOrigin(_config.Server);

            string signInPath;
            string loginJson;

            if (_config.UseV3Api)
            {
                signInPath = GlookoConstants.V3SignInPath;
                var loginData = new
                {
                    user = new
                    {
                        email = _config.Email,
                        password = _config.Password
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
                        email = _config.Email,
                        password = _config.Password
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
                return (null, DateTime.MinValue);
            }

            // Extract session cookie from response headers
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                foreach (var cookie in cookies)
                    if (cookie.StartsWith($"{GlookoConstants.SessionCookieName}="))
                    {
                        SessionCookie = cookie.Split(';')[0];
                        _logger.LogInformation("Session cookie extracted successfully");
                        break;
                    }

            // Parse user data from sign-in response (V2 only — V3 sign-in returns { success, two_fa_required })
            var responseJson = await GlookoHttpHelper.ReadResponseAsync(response, cancellationToken);
            if (!_config.UseV3Api)
            {
                try
                {
                    UserData = JsonSerializer.Deserialize<GlookoUserData>(responseJson);
                    if (UserData?.GlookoCode != null)
                        _logger.LogInformation(
                            "User data parsed successfully. Glooko code: {GlookoCode}",
                            UserData.GlookoCode);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not parse user data: {Message}", ex.Message);
                }
            }

            if (!string.IsNullOrEmpty(SessionCookie))
            {
                // V3 sign-in doesn't return user data — fetch it from /api/v3/session/users
                if (_config.UseV3Api)
                {
                    try
                    {
                        var userData = await FetchV3UserDataAsync(baseUrl, webOrigin, cancellationToken);
                        if (userData != null)
                        {
                            UserData = new GlookoUserData { User = new GlookoUserLogin { GlookoCode = userData.GlookoCode } };
                            _logger.LogInformation(
                                "V3 user profile loaded. Glooko code: {GlookoCode}, MeterUnits: {Units}",
                                userData.GlookoCode, userData.MeterUnits);
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
                return (SessionCookie, DateTime.UtcNow.Add(GlookoConstants.SessionLifetime));
            }

            _logger.LogError("Failed to extract session cookie from Glooko response");
            return (null, DateTime.MinValue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Glooko authentication error: {Message}", ex.Message);
            return (null, DateTime.MinValue);
        }
    }

    /// <summary>
    ///     Fetches the user profile from /api/v3/session/users after V3 sign-in.
    ///     The V3 sign-in response only contains { success, two_fa_required } —
    ///     the glookoCode and meter units come from this follow-up call.
    /// </summary>
    private async Task<GlookoV3User?> FetchV3UserDataAsync(
        string baseUrl, string webOrigin, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}{GlookoConstants.V3UsersPath}");
        GlookoHttpHelper.ApplyStandardHeaders(request, webOrigin, SessionCookie);

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
