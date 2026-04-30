using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
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
            _logger.LogInformation("Authenticating with Glooko server: {Server}", _config.Server);

            var baseUrl = GlookoConstants.ResolveBaseUrl(_config.Server);
            var webOrigin = GlookoConstants.ResolveWebOrigin(_config.Server);

            var loginData = new
            {
                userLogin = new
                {
                    email = _config.Email,
                    password = _config.Password
                },
                deviceInformation = GlookoConstants.DeviceInformation
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{GlookoConstants.SignInPath}")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(loginData),
                    Encoding.UTF8,
                    "application/json")
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

            // Parse user data
            var responseJson = await GlookoHttpHelper.ReadResponseAsync(response, cancellationToken);
            try
            {
                UserData = JsonSerializer.Deserialize<GlookoUserData>(responseJson);
                if (UserData?.UserLogin?.GlookoCode != null)
                    _logger.LogInformation(
                        "User data parsed successfully. Glooko code: {GlookoCode}",
                        UserData.UserLogin.GlookoCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not parse user data: {Message}", ex.Message);
            }

            if (!string.IsNullOrEmpty(SessionCookie))
            {
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
}

/// <summary>
///     Glooko user data returned from authentication.
/// </summary>
public class GlookoUserData
{
    [JsonPropertyName("userLogin")] public GlookoUserLogin? UserLogin { get; set; }
}

/// <summary>
///     Glooko user login details.
/// </summary>
public class GlookoUserLogin
{
    [JsonPropertyName("glookoCode")] public string? GlookoCode { get; set; }
}
