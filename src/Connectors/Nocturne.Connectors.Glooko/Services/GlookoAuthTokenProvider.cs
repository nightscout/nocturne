using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;

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

            // Setup headers to mimic browser behavior
            var loginData = new
            {
                userLogin = new
                {
                    email = _config.Email,
                    password = _config.Password
                },
                deviceInformation = new
                {
                    applicationType = "logbook",
                    os = "android",
                    osVersion = "33",
                    device = "Google Pixel 8 Pro",
                    deviceManufacturer = "Google",
                    deviceModel = "Pixel 8 Pro",
                    serialNumber = "HIDDEN",
                    clinicalResearch = false,
                    deviceId = "HIDDEN",
                    applicationVersion = "6.1.3",
                    buildNumber = "0",
                    gitHash = "g4fbed2011b"
                }
            };

            var json = JsonSerializer.Serialize(loginData);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/users/sign_in")
            {
                Content = content
            };

            // Add browser-like headers
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.5 Safari/605.1.15");
            request.Headers.TryAddWithoutValidation("Referer", "https://eu.my.glooko.com/");
            request.Headers.TryAddWithoutValidation("Origin", "https://eu.my.glooko.com");
            request.Headers.TryAddWithoutValidation("Connection", "keep-alive");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-GB,en;q=0.9");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await ReadResponseContentAsync(response, cancellationToken);
                _logger.LogError("Glooko authentication failed: {StatusCode} - {Error}",
                    response.StatusCode, errorContent);
                return (null, DateTime.MinValue);
            }

            // Extract session cookie from response headers
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                foreach (var cookie in cookies)
                    if (cookie.StartsWith("_logbook-web_session="))
                    {
                        SessionCookie = cookie.Split(';')[0];
                        _logger.LogInformation("Session cookie extracted successfully");
                        break;
                    }

            // Parse user data
            var responseJson = await ReadResponseContentAsync(response, cancellationToken);
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
                // Glooko sessions typically last 24 hours
                return (SessionCookie, DateTime.UtcNow.AddHours(24));
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

    private static async Task<string> ReadResponseContentAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        // Decompress if needed (check for gzip magic number 0x1F 0x8B)
        if (responseBytes.Length >= 2 && responseBytes[0] == 0x1F && responseBytes[1] == 0x8B)
        {
            using var compressedStream = new MemoryStream(responseBytes);
            using var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress);
            using var decompressedStream = new MemoryStream();
            await gzipStream.CopyToAsync(decompressedStream, cancellationToken);
            return Encoding.UTF8.GetString(decompressedStream.ToArray());
        }

        return Encoding.UTF8.GetString(responseBytes);
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