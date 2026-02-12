using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Nocturne.Widget.Contracts;

namespace Nocturne.Desktop.Tray.Services;

/// <summary>
/// Manages OAuth 2.0 Authorization Code + PKCE authentication for the tray app.
/// Uses a loopback HTTP listener (RFC 8252) to receive the authorization code,
/// then exchanges it for tokens at the OAuth token endpoint.
/// </summary>
public sealed class OidcAuthService : IDisposable
{
    private const string ClientId = "nocturne-tray";
    private const string Scopes = "entries.read treatments.read devicestatus.read profile.read";

    private readonly SettingsService _settingsService;
    private readonly ICredentialStore _credentialStore;
    private readonly HttpClient _httpClient;
    private Timer? _refreshTimer;

    // PKCE + loopback state (live between StartLogin and callback)
    private string? _codeVerifier;
    private string? _redirectUri;
    private HttpListener? _loopbackListener;

    /// <summary>
    /// Raised when the authentication state changes (signed in/out/refreshed).
    /// </summary>
    public event Action? AuthStateChanged;

    public bool IsAuthenticated { get; private set; }

    public OidcAuthService(SettingsService settingsService, ICredentialStore credentialStore)
    {
        _settingsService = settingsService;
        _credentialStore = credentialStore;
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Returns the current access token, or null if not authenticated.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync()
    {
        var creds = await _credentialStore.GetCredentialsAsync();
        return creds?.AccessToken;
    }

    /// <summary>
    /// Starts the OAuth 2.0 Authorization Code + PKCE flow.
    /// Opens the system browser to the OAuth authorize endpoint and starts
    /// a loopback HTTP listener to receive the authorization code callback.
    /// </summary>
    public void StartLogin()
    {
        var serverUrl = _settingsService.Settings.ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl))
            return;

        // Generate PKCE pair
        _codeVerifier = GenerateCodeVerifier();
        var codeChallenge = ComputeCodeChallenge(_codeVerifier);

        // Start loopback listener on a random port
        _redirectUri = StartLoopbackListener();
        if (_redirectUri is null)
            return;

        // Build OAuth authorize URL
        var authorizeUrl =
            $"{serverUrl}/api/oauth/authorize"
            + $"?response_type=code"
            + $"&client_id={Uri.EscapeDataString(ClientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(_redirectUri)}"
            + $"&scope={Uri.EscapeDataString(Scopes)}"
            + $"&code_challenge={Uri.EscapeDataString(codeChallenge)}"
            + $"&code_challenge_method=S256";

        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo
            {
                FileName = authorizeUrl,
                UseShellExecute = true,
            }
        );
    }

    /// <summary>
    /// Validates stored credentials on startup by attempting a token refresh.
    /// </summary>
    public async Task InitializeAsync()
    {
        var creds = await _credentialStore.GetCredentialsAsync();
        if (creds is null)
        {
            IsAuthenticated = false;
            return;
        }

        // If the access token hasn't expired, we're good
        if (creds.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            IsAuthenticated = true;
            var remainingSeconds = (int)(creds.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds;
            ScheduleRefresh(remainingSeconds);
            return;
        }

        // Otherwise try to refresh
        var refreshed = await RefreshTokensAsync();
        IsAuthenticated = refreshed;
    }

    /// <summary>
    /// Refreshes the access token using the stored refresh token via the OAuth token endpoint.
    /// </summary>
    public async Task<bool> RefreshTokensAsync()
    {
        var creds = await _credentialStore.GetCredentialsAsync();
        if (creds is null || string.IsNullOrEmpty(creds.RefreshToken))
            return false;

        try
        {
            var serverUrl = creds.ApiUrl.TrimEnd('/');
            var content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = creds.RefreshToken,
                    ["client_id"] = ClientId,
                }
            );

            var response = await _httpClient.PostAsync($"{serverUrl}/api/oauth/token", content);
            if (!response.IsSuccessStatusCode)
            {
                // If refresh token is invalid/revoked, clear credentials
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    await _credentialStore.DeleteCredentialsAsync();
                }
                IsAuthenticated = false;
                AuthStateChanged?.Invoke();
                return false;
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>();
            if (tokenResponse is null)
                return false;

            await _credentialStore.UpdateTokensAsync(
                tokenResponse.AccessToken,
                tokenResponse.RefreshToken,
                tokenResponse.ExpiresIn
            );

            IsAuthenticated = true;
            ScheduleRefresh(tokenResponse.ExpiresIn);
            AuthStateChanged?.Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Signs out by revoking the refresh token at the OAuth revocation endpoint (RFC 7009)
    /// and clearing local credentials.
    /// </summary>
    public async Task SignOutAsync()
    {
        var creds = await _credentialStore.GetCredentialsAsync();
        if (creds is not null)
        {
            try
            {
                var serverUrl = creds.ApiUrl.TrimEnd('/');
                var content = new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["token"] = creds.RefreshToken,
                        ["token_type_hint"] = "refresh_token",
                    }
                );
                await _httpClient.PostAsync($"{serverUrl}/api/oauth/revoke", content);
            }
            catch
            {
                // Best-effort server-side revocation
            }
        }

        await _credentialStore.DeleteCredentialsAsync();
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        StopLoopbackListener();
        IsAuthenticated = false;
        AuthStateChanged?.Invoke();
    }

    // ── Loopback HTTP listener ──────────────────────────────────────────

    private string? StartLoopbackListener()
    {
        StopLoopbackListener();

        var random = new Random();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var port = random.Next(49152, 65535);
            var prefix = $"http://127.0.0.1:{port}/callback/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                _loopbackListener = listener;
                _ = WaitForCallbackAsync(listener);
                return $"http://127.0.0.1:{port}/callback";
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }
        return null;
    }

    private void StopLoopbackListener()
    {
        if (_loopbackListener is not null)
        {
            try
            {
                _loopbackListener.Stop();
                _loopbackListener.Close();
            }
            catch
            { /* best effort */
            }
            _loopbackListener = null;
        }
    }

    private async Task WaitForCallbackAsync(HttpListener listener)
    {
        try
        {
            var context = await listener.GetContextAsync();
            var query = context.Request.QueryString;

            // Respond to the browser immediately
            var html = """
                <html><body style="font-family:system-ui;text-align:center;padding:60px">
                <h2>Authentication complete</h2>
                <p>You can close this tab and return to Nocturne Tray.</p>
                <script>window.close();</script>
                </body></html>
                """;
            var buffer = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer);
            context.Response.Close();

            StopLoopbackListener();

            // Check for error (e.g., user denied consent)
            var error = query["error"];
            if (!string.IsNullOrEmpty(error))
            {
                IsAuthenticated = false;
                AuthStateChanged?.Invoke();
                return;
            }

            // Extract authorization code
            var code = query["code"];
            if (string.IsNullOrEmpty(code))
            {
                IsAuthenticated = false;
                AuthStateChanged?.Invoke();
                return;
            }

            await ExchangeCodeForTokensAsync(code);
        }
        catch (ObjectDisposedException)
        {
            // Listener was stopped (e.g., user cancelled or signed out)
        }
        catch
        {
            IsAuthenticated = false;
            AuthStateChanged?.Invoke();
        }
    }

    // ── Token exchange ──────────────────────────────────────────────────

    private async Task ExchangeCodeForTokensAsync(string code)
    {
        var serverUrl = _settingsService.Settings.ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl) || _codeVerifier is null || _redirectUri is null)
        {
            IsAuthenticated = false;
            AuthStateChanged?.Invoke();
            return;
        }

        try
        {
            var content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = _redirectUri,
                    ["client_id"] = ClientId,
                    ["code_verifier"] = _codeVerifier,
                }
            );

            var response = await _httpClient.PostAsync($"{serverUrl}/api/oauth/token", content);
            if (!response.IsSuccessStatusCode)
            {
                IsAuthenticated = false;
                AuthStateChanged?.Invoke();
                return;
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>();
            if (tokenResponse is null)
            {
                IsAuthenticated = false;
                AuthStateChanged?.Invoke();
                return;
            }

            var scopes =
                tokenResponse.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

            var credentials = new NocturneCredentials
            {
                ApiUrl = serverUrl,
                AccessToken = tokenResponse.AccessToken,
                RefreshToken = tokenResponse.RefreshToken ?? "",
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn),
                Scopes = scopes.ToList().AsReadOnly(),
            };

            await _credentialStore.SaveCredentialsAsync(credentials);
            IsAuthenticated = true;
            ScheduleRefresh(tokenResponse.ExpiresIn);
            AuthStateChanged?.Invoke();
        }
        catch
        {
            IsAuthenticated = false;
            AuthStateChanged?.Invoke();
        }
        finally
        {
            _codeVerifier = null;
            _redirectUri = null;
        }
    }

    // ── PKCE (RFC 7636) ─────────────────────────────────────────────────

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    private static string ComputeCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    // ── Refresh scheduling ──────────────────────────────────────────────

    private void ScheduleRefresh(int expiresInSeconds)
    {
        _refreshTimer?.Dispose();

        // Refresh 2 minutes before expiry, minimum 30 seconds from now
        var refreshInMs = Math.Max(30_000, (expiresInSeconds - 120) * 1000);
        _refreshTimer = new Timer(
            _ =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RefreshTokensAsync();
                    }
                    catch
                    {
                        // Refresh failure is handled within RefreshTokensAsync;
                        // this catch prevents unobserved task exceptions.
                    }
                });
            },
            null,
            refreshInMs,
            Timeout.Infinite
        );
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        StopLoopbackListener();
        _httpClient.Dispose();
    }

    // ── Response model ──────────────────────────────────────────────────

    private sealed record OAuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; init; } = "";

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }

        [JsonPropertyName("token_type")]
        public string TokenType { get; init; } = "Bearer";

        [JsonPropertyName("scope")]
        public string? Scope { get; init; }
    }
}
