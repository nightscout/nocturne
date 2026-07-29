using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.CareLink.Configurations;
using Nocturne.Connectors.CareLink.Models;

namespace Nocturne.Connectors.CareLink.Services;

/// <summary>
/// Handles the Auth0 PKCE discovery and credential login flow for CareLink.
/// Separated from the token provider to keep token lifecycle management focused.
/// Uses a dedicated <see cref="HttpClient"/> with auto-redirect disabled so that
/// the redirect chain can be inspected manually to extract the authorization code.
/// </summary>
public partial class CareLinkAuthFlowService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    /// <param name="logger">Logger for the flow.</param>
    /// <param name="handler">
    /// Overrides the default handler. Tests pass a fake; production leaves it null.
    /// </param>
    public CareLinkAuthFlowService(ILogger logger, HttpMessageHandler? handler = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Auth0 PKCE flows require manual redirect inspection to capture the ?code= parameter
        // before the final redirect lands on a custom scheme URI the HttpClient cannot follow.
        // AllowAutoRedirect = false ensures we see every 302 response.
        _httpClient = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(2)
        };

        // CareLink's Auth0 tenant sits behind CloudFront, whose WAF rejects any request that carries
        // no User-Agent with a 403 "Request blocked" HTML page. HttpClient sends none by default, so
        // set it on the client rather than per-request — a call site that forgets it cannot work.
        _httpClient.DefaultRequestHeaders.Add("User-Agent", CareLinkConstants.UserAgents.MobileApp);
    }

    public void Dispose() => _httpClient.Dispose();

    public record AuthResult(string AccessToken, string RefreshToken, string ClientId, string TokenUrl, string? Audience);

    /// <summary>
    /// Performs the full Auth0 PKCE credential login flow.
    /// </summary>
    public async Task<AuthResult?> LoginAsync(string username, string password, string server, CancellationToken ct)
    {
        // 1-2. Discovery and Auth0 SSO config
        var ssoConfig = await FetchSsoConfigAsync(server, ct);
        if (ssoConfig == null)
            return null;

        var baseUrl = ssoConfig.GetBaseUrl();
        var tokenUrl = $"{baseUrl}{ssoConfig.SystemEndpoints.TokenEndpointPath}";

        // 3. PKCE
        var (codeVerifier, codeChallenge) = GeneratePkce();
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        // 4. GET authorize endpoint — follow redirects to login form
        var authorizeUrl = BuildAuthorizeUrl(baseUrl, ssoConfig, codeChallenge, state);
        _logger.LogDebug("Requesting authorize URL: {Url}", authorizeUrl);

        var authorizeResult = await FollowRedirectsToForm(authorizeUrl, ct);
        if (authorizeResult == null)
        {
            _logger.LogError("Failed to reach login form via authorize endpoint");
            return null;
        }

        // 5. Extract form fields and action URL
        var (formAction, hiddenFields) = ExtractFormData(authorizeResult.Html);
        if (formAction == null)
        {
            _logger.LogError("Could not extract login form action URL");
            return null;
        }

        // Resolve relative form action
        if (!formAction.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var pageUri = new Uri(authorizeResult.FinalUrl);
            formAction = new Uri(pageUri, formAction).ToString();
        }

        // 6. POST credentials
        hiddenFields["username"] = username;
        hiddenFields["password"] = password;
        hiddenFields["action"] = "default";

        using var formContent = new FormUrlEncodedContent(hiddenFields);
        using var postRequest = new HttpRequestMessage(HttpMethod.Post, formAction) { Content = formContent };

        var postResponse = await _httpClient.SendAsync(postRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        var postBody = await postResponse.Content.ReadAsStringAsync(ct);

        // Check for CAPTCHA
        if (postBody.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
            postBody.Contains("arkose", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "CareLink login requires CAPTCHA verification. Please obtain a refresh token externally " +
                "(e.g., using carelink-bridge's login tool) and configure it as the RefreshToken connector secret.");
            return null;
        }

        // Check for wrong credentials
        if (postBody.Contains("Wrong username or password", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("CareLink login failed: wrong username or password");
            return null;
        }

        // 7. Follow redirects to capture auth code
        var authCode = await ExtractAuthCode(postResponse, postBody, ct);
        if (authCode == null)
        {
            _logger.LogError("Failed to extract authorization code from redirect chain");
            return null;
        }

        // 8. Exchange code for tokens
        _logger.LogInformation("Exchanging authorization code for tokens");
        using var tokenContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ssoConfig.Client.ClientId,
            ["code"] = authCode,
            ["redirect_uri"] = ssoConfig.Client.RedirectUri,
            ["code_verifier"] = codeVerifier,
        });

        var tokenResponse = await _httpClient.PostAsync(tokenUrl, tokenContent, ct);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            var errorBody = await tokenResponse.Content.ReadAsStringAsync(ct);
            _logger.LogError("Token exchange failed with {StatusCode}: {Body}", tokenResponse.StatusCode, errorBody);
            return null;
        }

        var tokenJson = await tokenResponse.Content.ReadAsStringAsync(ct);
        using var tokenDoc = JsonDocument.Parse(tokenJson);
        var root = tokenDoc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString();
        var refreshToken = root.GetProperty("refresh_token").GetString();

        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
        {
            _logger.LogError("Token response missing access_token or refresh_token");
            return null;
        }

        return new AuthResult(accessToken, refreshToken, ssoConfig.Client.ClientId, tokenUrl, ssoConfig.Client.Audience);
    }

    /// <summary>
    /// Carries the PKCE verifier and token-exchange parameters produced by
    /// <see cref="BuildAuthorizeUrlAsync"/>. Everything except <see cref="AuthorizeUrl"/> must be
    /// stored server-side (keyed by <see cref="State"/>) and handed back to <see cref="ExchangeCodeAsync"/>.
    /// </summary>
    public record AuthorizeFlow(
        string AuthorizeUrl, string CodeVerifier, string State,
        string ClientId, string TokenUrl, string RedirectUri, string? Audience);

    /// <summary>
    /// Builds the Auth0 authorize URL for the user-driven ("manual-paste") connect flow. The user opens
    /// the URL in their own browser, signs in and solves the CAPTCHA, and returns the resulting code to
    /// <see cref="ExchangeCodeAsync"/>. This is the only viable interactive path — Medtronic's Auth0
    /// client only permits the <c>com.medtronic.carepartner:/sso</c> redirect, so no web callback can
    /// receive the code, and headless login is CAPTCHA-blocked.
    /// </summary>
    public async Task<AuthorizeFlow?> BuildAuthorizeUrlAsync(string server, CancellationToken ct)
    {
        var ssoConfig = await FetchSsoConfigAsync(server, ct);
        if (ssoConfig == null)
            return null;

        var baseUrl = ssoConfig.GetBaseUrl();
        var tokenUrl = $"{baseUrl}{ssoConfig.SystemEndpoints.TokenEndpointPath}";
        var (codeVerifier, codeChallenge) = GeneratePkce();
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var authorizeUrl = BuildAuthorizeUrl(baseUrl, ssoConfig, codeChallenge, state);

        return new AuthorizeFlow(
            authorizeUrl, codeVerifier, state,
            ssoConfig.Client.ClientId, tokenUrl, ssoConfig.Client.RedirectUri, ssoConfig.Client.Audience);
    }

    /// <summary>
    /// Exchanges the authorization code (captured by the user from the redirect) for tokens, using the
    /// PKCE verifier and parameters from the corresponding <see cref="AuthorizeFlow"/>.
    /// </summary>
    public async Task<AuthResult?> ExchangeCodeAsync(
        string code, string codeVerifier, string clientId, string tokenUrl, string redirectUri,
        string? audience, CancellationToken ct)
    {
        using var tokenContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        });

        var tokenResponse = await _httpClient.PostAsync(tokenUrl, tokenContent, ct);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            var err = await tokenResponse.Content.ReadAsStringAsync(ct);
            _logger.LogError("CareLink code exchange failed with {StatusCode}: {Body}", tokenResponse.StatusCode, err);
            return null;
        }

        var tokenJson = await tokenResponse.Content.ReadAsStringAsync(ct);
        using var tokenDoc = JsonDocument.Parse(tokenJson);
        var root = tokenDoc.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
        {
            _logger.LogError("CareLink code exchange response missing access_token or refresh_token");
            return null;
        }

        return new AuthResult(accessToken, refreshToken, clientId, tokenUrl, audience);
    }

    /// <summary>
    /// The Auth0 client parameters a refresh-token grant needs. These are public values published in
    /// Medtronic's own discovery config, not user secrets.
    /// </summary>
    public record SsoParameters(string ClientId, string TokenUrl, string? Audience);

    /// <summary>
    /// Resolves the region's Auth0 client id, token endpoint and audience from CareLink's discovery
    /// config. Lets a refresh token acquired outside the connect flow (pasted into the connector
    /// settings) be redeemed without the user having to supply those values. Returns null if
    /// discovery is unreachable or malformed — the caller then has no way to refresh.
    /// </summary>
    public async Task<SsoParameters?> ResolveSsoParametersAsync(string server, CancellationToken ct)
    {
        try
        {
            var ssoConfig = await FetchSsoConfigAsync(server, ct);
            if (ssoConfig == null)
                return null;

            var tokenUrl = $"{ssoConfig.GetBaseUrl()}{ssoConfig.SystemEndpoints.TokenEndpointPath}";
            return new SsoParameters(ssoConfig.Client.ClientId, tokenUrl, ssoConfig.Client.Audience);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Could not resolve CareLink SSO parameters for region {Server}", server);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not resolve CareLink SSO parameters for region {Server}", server);
            return null;
        }
    }

    /// <summary>
    /// Fetches the region's Auth0 SSO config: discovery, then the SSO config document it points to.
    /// </summary>
    private async Task<Auth0SSOConfig?> FetchSsoConfigAsync(string server, CancellationToken ct)
    {
        var discoveryUrl = GetDiscoveryUrl(server);
        _logger.LogDebug("Fetching CareLink discovery config from {Url}", discoveryUrl);

        var discoveryResponse = await _httpClient.GetAsync(discoveryUrl, ct);
        discoveryResponse.EnsureSuccessStatusCode();
        var discovery = JsonSerializer.Deserialize<DiscoverResponse>(
            await discoveryResponse.Content.ReadAsStringAsync(ct));

        var ssoConfigUrl = ResolveSSOConfigUrl(discovery, server);
        if (ssoConfigUrl == null)
        {
            _logger.LogError("Could not resolve SSO config URL from discovery response for region {Server}", server);
            return null;
        }

        _logger.LogDebug("Fetching Auth0 SSO config from {Url}", ssoConfigUrl);
        var ssoResponse = await _httpClient.GetAsync(ssoConfigUrl, ct);
        ssoResponse.EnsureSuccessStatusCode();
        var ssoConfig = JsonSerializer.Deserialize<Auth0SSOConfig>(
            await ssoResponse.Content.ReadAsStringAsync(ct));
        if (ssoConfig == null)
            _logger.LogError("Failed to deserialize Auth0 SSO config");

        return ssoConfig;
    }

    /// <summary>
    /// Fetches the authenticated user's CareLink profile (username, country) with a freshly-issued
    /// access token, so the connect flow can auto-fill the connector configuration.
    /// </summary>
    public async Task<CareLinkUserInfo?> FetchUserInfoAsync(string accessToken, string server, CancellationToken ct)
    {
        var host = server.Equals("US", StringComparison.OrdinalIgnoreCase)
            ? CareLinkConstants.Servers.Us
            : CareLinkConstants.Servers.Eu;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}{CareLinkConstants.Endpoints.UsersMe}");
        request.Headers.Add("Authorization", $"Bearer {accessToken}");

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        return JsonSerializer.Deserialize<CareLinkUserInfo>(await response.Content.ReadAsStringAsync(ct));
    }

    // --- Static helpers ---

    public static string GetDiscoveryUrl(string server)
    {
        var baseUrl = server.ToUpperInvariant() switch
        {
            "US" => CareLinkConstants.Discovery.UsBaseUrl,
            _ => CareLinkConstants.Discovery.EuBaseUrl,
        };
        return $"{baseUrl}{CareLinkConstants.Discovery.DiscoveryPath}";
    }

    public static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Base64UrlEncode(verifierBytes);
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Base64UrlEncode(challengeBytes);
        return (verifier, challenge);
    }

    // --- Private helpers ---

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string BuildAuthorizeUrl(string baseUrl, Auth0SSOConfig config, string codeChallenge, string state)
    {
        var path = config.SystemEndpoints.AuthorizationEndpointPath;
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = config.Client.ClientId;
        query["response_type"] = "code";
        query["scope"] = config.Client.Scope;
        query["audience"] = config.Client.Audience;
        query["redirect_uri"] = config.Client.RedirectUri;
        query["code_challenge"] = codeChallenge;
        query["code_challenge_method"] = "S256";
        query["state"] = state;
        return $"{baseUrl}{path}?{query}";
    }

    private static string? ResolveSSOConfigUrl(DiscoverResponse? discovery, string server)
    {
        if (discovery?.Cp == null) return null;

        var region = server.ToUpperInvariant();
        var entry = discovery.Cp.FirstOrDefault(e =>
            e.Region?.Equals(region, StringComparison.OrdinalIgnoreCase) == true);

        if (entry == null && discovery.Cp.Count > 0)
            entry = discovery.Cp[0];

        if (entry == null) return null;

        var configKey = entry.UseSSOConfiguration ?? CareLinkConstants.Discovery.DefaultSSOConfigKey;
        return configKey == CareLinkConstants.Discovery.DefaultSSOConfigKey
            ? entry.Auth0SSOConfiguration
            : null;
    }

    private record FormPageResult(string Html, string FinalUrl);

    private async Task<FormPageResult?> FollowRedirectsToForm(string url, CancellationToken ct, int maxRedirects = 10)
    {
        var currentUrl = url;
        for (var i = 0; i < maxRedirects; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.Headers.Location != null && (int)response.StatusCode is >= 300 and < 400)
            {
                currentUrl = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location.ToString()
                    : new Uri(new Uri(currentUrl), response.Headers.Location).ToString();
                continue;
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            if (html.Contains("<form", StringComparison.OrdinalIgnoreCase))
                return new FormPageResult(html, currentUrl);
        }

        return null;
    }

    private static (string? Action, Dictionary<string, string> Fields) ExtractFormData(string html)
    {
        var fields = new Dictionary<string, string>();

        var formMatch = FormActionRegex().Match(html);
        var action = formMatch.Success
            ? HttpUtility.HtmlDecode(formMatch.Groups[1].Value.Length > 0 ? formMatch.Groups[1].Value : formMatch.Groups[2].Value)
            : null;

        foreach (Match match in HiddenInputRegex().Matches(html))
        {
            var name = HttpUtility.HtmlDecode(match.Groups[1].Value);
            var value = HttpUtility.HtmlDecode(match.Groups[2].Value);
            if (!string.IsNullOrEmpty(name))
                fields[name] = value ?? "";
        }

        return (action, fields);
    }

    private async Task<string?> ExtractAuthCode(HttpResponseMessage response, string body, CancellationToken ct)
    {
        var code = TryExtractCodeFromLocation(response);
        if (code != null) return code;

        for (var i = 0; i < 15; i++)
        {
            var location = response.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(location)) break;

            code = TryExtractCodeFromUrl(location);
            if (code != null) return code;

            if (!location.StartsWith("http", StringComparison.OrdinalIgnoreCase)) break;

            using var redirectRequest = new HttpRequestMessage(HttpMethod.Get, location);
            response = await _httpClient.SendAsync(redirectRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            code = TryExtractCodeFromLocation(response);
            if (code != null) return code;
        }

        var redirectMatch = Regex.Match(body, @"[""']([^""']*[?&]code=[^""']*)[""']");
        if (redirectMatch.Success)
            return TryExtractCodeFromUrl(redirectMatch.Groups[1].Value);

        return null;
    }

    private static string? TryExtractCodeFromLocation(HttpResponseMessage response)
    {
        var location = response.Headers.Location?.ToString();
        return location == null ? null : TryExtractCodeFromUrl(location);
    }

    private static string? TryExtractCodeFromUrl(string url)
    {
        var match = Regex.Match(url, @"[?&]code=([^&]+)");
        return match.Success ? HttpUtility.UrlDecode(match.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"<form[^>]*action=""([^""]*)""|<form[^>]*action='([^']*)'", RegexOptions.IgnoreCase)]
    private static partial Regex FormActionRegex();

    [GeneratedRegex(@"<input[^>]*type=""hidden""[^>]*name=""([^""]*)""\s*value=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenInputRegex();
}
