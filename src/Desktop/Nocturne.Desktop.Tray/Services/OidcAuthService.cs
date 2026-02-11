using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Desktop.Tray.Services;

/// <summary>
/// Manages the OIDC authentication lifecycle for the desktop tray app.
///
/// Login flow:
///   1. Open system browser to {serverUrl}/auth/login?returnUrl=nocturne-tray://auth/callback
///   2. User authenticates with their OIDC provider via the Nocturne web UI
///   3. Server completes the exchange and redirects to nocturne-tray://auth/callback
///      with access_token and refresh_token as query parameters
///   4. Protocol activation delivers the tokens to HandleCallbackAsync
///
/// Token refresh runs on a background timer, refreshing the access token
/// before it expires using POST /auth/refresh with the opaque refresh token.
/// </summary>
public sealed class OidcAuthService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly HttpClient _httpClient;
    private Timer? _refreshTimer;
    private DateTimeOffset _accessTokenExpiry = DateTimeOffset.MinValue;

    public event Action? AuthStateChanged;
    public event Action<string>? AuthError;

    public bool IsAuthenticated => _settingsService.IsAuthenticated;

    public OidcAuthService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Opens the system browser to initiate the OIDC login flow.
    /// The Nocturne server handles the OIDC provider interaction and
    /// redirects back to our protocol handler with tokens.
    /// </summary>
    public void StartLogin()
    {
        var serverUrl = _settingsService.Settings.ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl))
        {
            AuthError?.Invoke("Server URL is not configured.");
            return;
        }

        var callbackUri = Uri.EscapeDataString("nocturne-tray://auth/callback");
        var loginUrl = $"{serverUrl}/auth/login?returnUrl={callbackUri}&client_type=native";

        // Launch system browser
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = loginUrl,
            UseShellExecute = true,
        };
        System.Diagnostics.Process.Start(startInfo);
    }

    /// <summary>
    /// Handles the protocol activation callback from the OIDC flow.
    /// Extracts tokens from the URI and stores them securely.
    /// </summary>
    public async Task<bool> HandleCallbackAsync(Uri callbackUri)
    {
        var query = System.Web.HttpUtility.ParseQueryString(callbackUri.Query);

        var accessToken = query["access_token"] ?? query["token"];
        var refreshToken = query["refresh_token"] ?? query["refresh"];
        var expiresIn = query["expires_in"];
        var error = query["error"];

        if (!string.IsNullOrEmpty(error))
        {
            var description = query["error_description"] ?? error;
            AuthError?.Invoke($"Authentication failed: {description}");
            return false;
        }

        if (string.IsNullOrEmpty(accessToken))
        {
            AuthError?.Invoke("No access token received from server.");
            return false;
        }

        _settingsService.SetAccessToken(accessToken);

        if (!string.IsNullOrEmpty(refreshToken))
        {
            _settingsService.SetRefreshToken(refreshToken);
        }

        if (int.TryParse(expiresIn, out var seconds))
        {
            _accessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }
        else
        {
            // Default to 15 minutes if not provided (Nocturne default)
            _accessTokenExpiry = DateTimeOffset.UtcNow.AddMinutes(15);
        }

        StartRefreshTimer();
        AuthStateChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Initializes the auth service on app startup. If tokens exist,
    /// attempts an immediate refresh to validate them and starts the
    /// background refresh timer.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (!_settingsService.IsAuthenticated) return;

        ConfigureHttpClient();

        // Try to refresh immediately to validate stored tokens
        var refreshed = await RefreshTokensAsync();
        if (refreshed)
        {
            StartRefreshTimer();
        }
        else
        {
            // Stored tokens are invalid, clear them
            _settingsService.ClearTokens();
            AuthStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Returns the current access token for API calls.
    /// Returns null if not authenticated.
    /// </summary>
    public string? GetAccessToken() => _settingsService.GetAccessToken();

    /// <summary>
    /// Signs out by revoking the refresh token on the server,
    /// clearing stored credentials, and stopping the refresh timer.
    /// </summary>
    public async Task SignOutAsync()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;

        var serverUrl = _settingsService.Settings.ServerUrl?.TrimEnd('/');
        var refreshToken = _settingsService.GetRefreshToken();

        // Best-effort server-side logout
        if (!string.IsNullOrEmpty(serverUrl) && !string.IsNullOrEmpty(refreshToken))
        {
            try
            {
                ConfigureHttpClient();
                using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
                request.Headers.Add("Refresh", refreshToken);
                await _httpClient.SendAsync(request);
            }
            catch
            {
                // Server unreachable; local cleanup still proceeds
            }
        }

        _settingsService.ClearTokens();
        _accessTokenExpiry = DateTimeOffset.MinValue;
        AuthStateChanged?.Invoke();
    }

    /// <summary>
    /// Refreshes the access token using the stored refresh token.
    /// Called by the background timer and on-demand before token expiry.
    /// </summary>
    private async Task<bool> RefreshTokensAsync()
    {
        var refreshToken = _settingsService.GetRefreshToken();
        if (string.IsNullOrEmpty(refreshToken)) return false;

        try
        {
            ConfigureHttpClient();

            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
            // Nocturne accepts the refresh token via the Refresh header for API clients
            request.Headers.Add("Refresh", refreshToken);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    // Refresh token revoked or expired — user must re-authenticate
                    _settingsService.ClearTokens();
                    AuthStateChanged?.Invoke();
                }
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<TokenRefreshResponse>();
            if (result is null || string.IsNullOrEmpty(result.AccessToken)) return false;

            _settingsService.SetAccessToken(result.AccessToken);

            // Store rotated refresh token if the server issued a new one
            if (!string.IsNullOrEmpty(result.RefreshToken))
            {
                _settingsService.SetRefreshToken(result.RefreshToken);
            }

            _accessTokenExpiry = result.ExpiresAt != default
                ? result.ExpiresAt
                : DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn > 0 ? result.ExpiresIn : 900);

            AuthStateChanged?.Invoke();
            return true;
        }
        catch
        {
            // Network error — don't clear tokens, retry on next timer tick
            return false;
        }
    }

    private void StartRefreshTimer()
    {
        _refreshTimer?.Dispose();

        // Refresh 2 minutes before expiry, minimum 30 seconds from now
        var refreshIn = _accessTokenExpiry - DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
        if (refreshIn < TimeSpan.FromSeconds(30))
        {
            refreshIn = TimeSpan.FromSeconds(30);
        }

        _refreshTimer = new Timer(async _ =>
        {
            var success = await RefreshTokensAsync();
            if (success)
            {
                StartRefreshTimer();
            }
        }, null, refreshIn, Timeout.InfiniteTimeSpan);
    }

    private void ConfigureHttpClient()
    {
        var serverUrl = _settingsService.Settings.ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl)) return;

        if (_httpClient.BaseAddress?.ToString().TrimEnd('/') != serverUrl)
        {
            _httpClient.BaseAddress = new Uri(serverUrl);
        }

        _httpClient.DefaultRequestHeaders.Clear();
        var token = _settingsService.GetAccessToken();
        if (!string.IsNullOrEmpty(token))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _httpClient.Dispose();
    }

    private sealed record TokenRefreshResponse
    {
        [JsonPropertyName("accessToken")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refreshToken")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("tokenType")]
        public string? TokenType { get; init; }

        [JsonPropertyName("expiresIn")]
        public int ExpiresIn { get; init; }

        [JsonPropertyName("expiresAt")]
        public DateTimeOffset ExpiresAt { get; init; }

        [JsonPropertyName("subjectId")]
        public string? SubjectId { get; init; }
    }
}
