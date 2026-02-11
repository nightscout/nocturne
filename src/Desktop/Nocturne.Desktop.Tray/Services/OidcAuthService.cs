using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Nocturne.Widget.Contracts;

namespace Nocturne.Desktop.Tray.Services;

/// <summary>
/// Manages the OIDC browser-based authentication lifecycle for the tray app.
/// Uses ICredentialStore from Widget.Contracts for secure token persistence.
/// </summary>
public sealed class OidcAuthService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly ICredentialStore _credentialStore;
    private readonly HttpClient _httpClient;
    private Timer? _refreshTimer;

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
    /// Opens the system browser to the Nocturne OIDC login page.
    /// The server will redirect back to nocturne-tray://auth/callback with tokens.
    /// </summary>
    public void StartLogin()
    {
        var serverUrl = _settingsService.Settings.ServerUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(serverUrl)) return;

        var callbackUri = Uri.EscapeDataString("nocturne-tray://auth/callback");
        var loginUrl = $"{serverUrl}/auth/login?returnUrl={callbackUri}";

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = loginUrl,
            UseShellExecute = true,
        };
        System.Diagnostics.Process.Start(startInfo);
    }

    /// <summary>
    /// Handles the protocol activation callback from the OIDC flow.
    /// Parses tokens from the URI and stores them via ICredentialStore.
    /// </summary>
    public async Task<bool> HandleCallbackAsync(Uri callbackUri)
    {
        var query = System.Web.HttpUtility.ParseQueryString(callbackUri.Query);

        // Check for error response
        var error = query["error"];
        if (!string.IsNullOrEmpty(error))
        {
            IsAuthenticated = false;
            AuthStateChanged?.Invoke();
            return false;
        }

        var accessToken = query["access_token"];
        var refreshToken = query["refresh_token"];
        var expiresInStr = query["expires_in"];

        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
        {
            return false;
        }

        var expiresIn = int.TryParse(expiresInStr, out var exp) ? exp : 900;
        var serverUrl = _settingsService.Settings.ServerUrl?.TrimEnd('/') ?? "";

        var credentials = new NocturneCredentials
        {
            ApiUrl = serverUrl,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            Scopes = ["entries.read", "treatments.read", "devicestatus.read", "profile.read"],
        };

        await _credentialStore.SaveCredentialsAsync(credentials);
        IsAuthenticated = true;
        ScheduleRefresh(expiresIn);
        AuthStateChanged?.Invoke();
        return true;
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
    /// Refreshes the access token using the stored refresh token.
    /// </summary>
    public async Task<bool> RefreshTokensAsync()
    {
        var creds = await _credentialStore.GetCredentialsAsync();
        if (creds is null) return false;

        try
        {
            var serverUrl = creds.ApiUrl.TrimEnd('/');
            var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/auth/refresh");
            request.Headers.Add("Refresh", creds.RefreshToken);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                IsAuthenticated = false;
                AuthStateChanged?.Invoke();
                return false;
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<TokenRefreshResponse>();
            if (tokenResponse is null) return false;

            await _credentialStore.UpdateTokensAsync(
                tokenResponse.AccessToken,
                tokenResponse.RefreshToken,
                tokenResponse.ExpiresIn);

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
    /// Signs out by revoking the refresh token on the server and clearing local credentials.
    /// </summary>
    public async Task SignOutAsync()
    {
        var creds = await _credentialStore.GetCredentialsAsync();
        if (creds is not null)
        {
            try
            {
                var serverUrl = creds.ApiUrl.TrimEnd('/');
                var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/auth/logout");
                request.Headers.Add("Refresh", creds.RefreshToken);
                await _httpClient.SendAsync(request);
            }
            catch
            {
                // Best-effort server-side revocation
            }
        }

        await _credentialStore.DeleteCredentialsAsync();
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        IsAuthenticated = false;
        AuthStateChanged?.Invoke();
    }

    private void ScheduleRefresh(int expiresInSeconds)
    {
        _refreshTimer?.Dispose();

        // Refresh 2 minutes before expiry, minimum 30 seconds from now
        var refreshInMs = Math.Max(30_000, (expiresInSeconds - 120) * 1000);
        _refreshTimer = new Timer(async _ => await RefreshTokensAsync(), null, refreshInMs, Timeout.Infinite);
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _httpClient.Dispose();
    }

    private sealed record TokenRefreshResponse
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; init; } = "";

        [JsonPropertyName("refreshToken")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expiresIn")]
        public int ExpiresIn { get; init; }

        [JsonPropertyName("tokenType")]
        public string TokenType { get; init; } = "Bearer";
    }
}
