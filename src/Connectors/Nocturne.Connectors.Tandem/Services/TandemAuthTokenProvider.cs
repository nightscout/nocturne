using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tandem.Configurations;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.Tandem.Services;

/// <summary>
/// Authenticates against the Tandem Source cloud using its OpenID Connect Authorization-Code +
/// PKCE flow, and caches the resulting access token (and the pumper/account ids extracted from the
/// id_token) per tenant. Ported from <c>tconnectsync</c>'s <c>api/tandemsource.py</c> login flow.
/// </summary>
public class TandemAuthTokenProvider(
    HttpClient httpClient,
    IConnectorTokenCache tokenCache,
    IConnectorServerResolver<TandemConnectorConfiguration> serverResolver,
    ITenantAccessor tenantAccessor,
    ILogger<TandemAuthTokenProvider> logger,
    IRetryDelayStrategy retryDelayStrategy)
    : AuthTokenProviderBase<TandemConnectorConfiguration>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
{
    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    /// <summary>Metadata key under which the resolved pumper id is cached alongside the token.</summary>
    public const string PumperIdKey = "PumperId";

    /// <summary>Metadata key under which the resolved account id is cached alongside the token.</summary>
    public const string AccountIdKey = "AccountId";

    /// <summary>Access tokens are short-lived; refresh a few minutes early.</summary>
    protected override int TokenLifetimeBufferMinutes => 5;

    protected override string ConnectorName => "Tandem";

    protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)>
        AcquireTokenAsync(TandemConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var region = TandemConstants.ForRegion(config.Region);
        var maxRetries = LoginAttempts(config);
        var expiresAt = DateTime.MinValue;
        IReadOnlyDictionary<string, string>? metadata = null;

        var accessToken = await ExecuteWithRetryAsync<string>(
            async attempt =>
            {
                _logger.LogInformation(
                    "Authenticating with Tandem Source (region {Region}, attempt {Attempt}/{MaxRetries})",
                    region == TandemConstants.Eu ? "EU" : "US", attempt + 1, maxRetries);

                var signIn = await SignInAsync(region, config, cancellationToken);
                if (signIn.Token == null)
                    return (null, signIn.ShouldRetry);

                expiresAt = signIn.ExpiresAt;
                metadata = signIn.Metadata;
                return (signIn.Token, false);
            },
            _retryDelayStrategy,
            maxRetries,
            "Tandem Source authentication",
            cancellationToken);

        if (string.IsNullOrEmpty(accessToken))
            return (null, DateTime.MinValue, null);

        _logger.LogInformation(
            "Tandem Source authentication successful (region {Region}), token expires at {ExpiresAt}",
            region == TandemConstants.Eu ? "EU" : "US", expiresAt);

        return (accessToken, expiresAt, metadata);
    }

    /// <summary>
    ///     Runs one full OIDC sign-in, returning the access token plus what to cache with it, or null
    ///     plus whether the failure is worth another attempt.
    /// </summary>
    private async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata, bool ShouldRetry)>
        SignInAsync(
            TandemConstants.RegionUrls region, TandemConnectorConfiguration config,
            CancellationToken cancellationToken)
    {
        // One cookie jar per login attempt, matching tconnectsync's fresh requests.Session.
        using var client = CreateLoginClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(TandemConstants.UserAgent);

        try
        {
            // 1. Prime the SSO session, then post credentials to the login API.
            await client.GetAsync(TandemConstants.LoginPageUrl, cancellationToken);

            var login = await LoginAsync(client, region, config, cancellationToken);
            if (!login.Succeeded)
                return (null, DateTime.MinValue, null, login.ShouldRetry);

            // 2. PKCE authorize → authorization code.
            var (verifier, challenge) = GeneratePkcePair();
            var (code, authorizeShouldRetry) = await AuthorizeAsync(client, region, challenge, cancellationToken);
            if (code == null)
                return (null, DateTime.MinValue, null, authorizeShouldRetry);

            // 3. Exchange the code for tokens.
            var (tokens, exchangeShouldRetry) = await ExchangeCodeAsync(client, region, code, verifier, cancellationToken);
            if (tokens?.AccessToken == null || tokens.IdToken == null)
                return (null, DateTime.MinValue, null, exchangeShouldRetry);

            // 4. Extract pumper/account ids from the id_token claims.
            var (pumperId, accountId) = ExtractIds(tokens.IdToken);
            if (pumperId == null)
            {
                _logger.LogError("Tandem id_token did not contain a pumperId claim");
                return (null, DateTime.MinValue, null, false);
            }

            var metadata = new Dictionary<string, string> { [PumperIdKey] = pumperId };
            if (accountId != null)
                metadata[AccountIdKey] = accountId;

            var expiresAt = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600);

            return (tokens.AccessToken, expiresAt, metadata, false);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Tandem Source authentication response");
            return (null, DateTime.MinValue, null, false);
        }
    }

    /// <summary>
    ///     Creates the cookie-jar-per-attempt client the sign-in walks its redirect chain on.
    ///     Overridden in tests to supply a fake transport.
    /// </summary>
    protected virtual HttpClient CreateLoginClient() => OutboundHttpClient.CreateIsolated(followRedirects: true);

    private async Task<(bool Succeeded, bool ShouldRetry)> LoginAsync(
        HttpClient client, TandemConstants.RegionUrls region,
        TandemConnectorConfiguration config, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, region.LoginApiUrl);
        request.Headers.Referrer = new Uri(TandemConstants.LoginPageUrl);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { username = config.Email, password = config.Password }),
            Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Tandem Source login failed with HTTP {StatusCode}", (int)response.StatusCode);
            return (false, response.IsRetryableError());
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
        if (!string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            // Tandem answers a rejected credential with HTTP 200 and a non-SUCCESS status in the
            // body, so this — not the status line — is the sign that retrying cannot help.
            _logger.LogError("Tandem Source login did not return SUCCESS (status: {Status})", status);
            return (false, false);
        }

        return (true, false);
    }

    private async Task<(string? Code, bool ShouldRetry)> AuthorizeAsync(
        HttpClient client, TandemConstants.RegionUrls region, string codeChallenge,
        CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = region.ClientId;
        query["response_type"] = "code";
        query["scope"] = "openid profile email";
        query["redirect_uri"] = region.RedirectUri;
        query["code_challenge"] = codeChallenge;
        query["code_challenge_method"] = "S256";

        var url = $"{region.AuthorizationEndpoint}?{query}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri(TandemConstants.LoginPageUrl);

        var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Tandem Source authorize step failed with HTTP {StatusCode}", (int)response.StatusCode);
            return (null, response.IsRetryableError());
        }

        // After following redirects, the authorization code is in the final request URL's query.
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri == null)
            return (null, false);

        var code = HttpUtility.ParseQueryString(finalUri.Query)["code"];
        if (string.IsNullOrEmpty(code))
        {
            _logger.LogError("Tandem Source authorize step returned no code in redirect URL");
            return (null, false);
        }

        return (code, false);
    }

    private async Task<(TandemTokenResponse? Tokens, bool ShouldRetry)> ExchangeCodeAsync(
        HttpClient client, TandemConstants.RegionUrls region, string code, string codeVerifier,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, region.TokenEndpoint);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = region.ClientId,
            ["code"] = code,
            ["redirect_uri"] = region.RedirectUri,
            ["code_verifier"] = codeVerifier,
        });

        var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Tandem Source token exchange failed with HTTP {StatusCode}", (int)response.StatusCode);
            return (null, response.IsRetryableError());
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var tokens = await JsonSerializer.DeserializeAsync<TandemTokenResponse>(
            stream, cancellationToken: cancellationToken);

        return (tokens, false);
    }

    /// <summary>
    /// Extracts the pumperId and accountId claims from the id_token's payload. The token was just
    /// issued by Tandem's token endpoint over TLS within this same login; we read its claims for the
    /// ids rather than re-verifying the RS256 signature (no untrusted party is involved).
    /// </summary>
    private (string? PumperId, string? AccountId) ExtractIds(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length < 2)
            return (null, null);

        try
        {
            var payload = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var pumperId = ReadClaim(root, "pumperId");
            var accountId = ReadClaim(root, "accountId");
            return (pumperId, accountId);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            _logger.LogError(ex, "Failed to decode Tandem id_token claims");
            return (null, null);
        }
    }

    private static string? ReadClaim(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.Number
            ? value.GetRawText()
            : value.GetString();
    }

    private static (string Verifier, string Challenge) GeneratePkcePair()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(64);
        var verifier = Base64UrlEncode(verifierBytes);
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };
        return Convert.FromBase64String(padded);
    }

    private sealed class TandemTokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("id_token")]
        public string? IdToken { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
