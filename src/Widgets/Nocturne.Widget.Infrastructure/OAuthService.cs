using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nocturne.Widget.Contracts;

namespace Nocturne.Widget.Infrastructure;

/// <summary>
/// OAuth service implementation for device authorization flow
/// </summary>
public class OAuthService : IOAuthService
{
    private const string ClientId = "nocturne-widget-windows11";
    private const string DefaultScopes =
        "entries.read treatments.read devicestatus.read profile.read";

    private readonly HttpClient _httpClient;
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger<OAuthService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public OAuthService(
        HttpClient httpClient,
        ICredentialStore credentialStore,
        ILogger<OAuthService> logger
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialStore =
            credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<DeviceAuthorizationResult> InitiateDeviceAuthorizationAsync(
        string apiUrl,
        IEnumerable<string>? scopes = null
    )
    {
        try
        {
            var scopeString = scopes != null ? string.Join(" ", scopes) : DefaultScopes;
            var requestUri = $"{apiUrl.TrimEnd('/')}/api/oauth/device";

            var content = new FormUrlEncodedContent(
                new Dictionary<string, string> { ["client_id"] = ClientId, ["scope"] = scopeString }
            );

            var response = await _httpClient.PostAsync(requestUri, content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadFromJsonAsync<OAuthErrorResponse>(
                    JsonOptions
                );
                _logger.LogWarning("Device authorization failed: {Error}", error?.Error);
                return new DeviceAuthorizationResult
                {
                    Success = false,
                    Error = error?.ErrorDescription ?? error?.Error ?? "Unknown error",
                };
            }

            var deviceResponse =
                await response.Content.ReadFromJsonAsync<DeviceAuthorizationResponse>(JsonOptions);
            if (deviceResponse is null)
            {
                return new DeviceAuthorizationResult
                {
                    Success = false,
                    Error = "Invalid response from server",
                };
            }

            var state = new DeviceAuthorizationState
            {
                ApiUrl = apiUrl,
                DeviceCode = deviceResponse.DeviceCode,
                UserCode = deviceResponse.UserCode,
                VerificationUri = deviceResponse.VerificationUri,
                VerificationUriComplete = deviceResponse.VerificationUriComplete,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(deviceResponse.ExpiresIn),
                Interval = deviceResponse.Interval,
            };

            await _credentialStore.SaveDeviceAuthStateAsync(state);

            _logger.LogInformation(
                "Device authorization initiated. User code: {UserCode}, Verification: {Uri}",
                state.UserCode,
                state.VerificationUri
            );

            return new DeviceAuthorizationResult { Success = true, State = state };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating device authorization");
            return new DeviceAuthorizationResult { Success = false, Error = ex.Message };
        }
    }

    /// <inheritdoc />
    public async Task<DevicePollResult> PollForAuthorizationAsync()
    {
        try
        {
            var state = await _credentialStore.GetDeviceAuthStateAsync();
            if (state is null)
            {
                return new DevicePollResult
                {
                    Success = false,
                    Error = "No pending device authorization",
                };
            }

            if (DateTimeOffset.UtcNow >= state.ExpiresAt)
            {
                await _credentialStore.ClearDeviceAuthStateAsync();
                return new DevicePollResult
                {
                    Success = false,
                    Expired = true,
                    Error = "Device code has expired",
                };
            }

            var requestUri = $"{state.ApiUrl.TrimEnd('/')}/api/oauth/token";
            var content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = state.DeviceCode,
                    ["client_id"] = ClientId,
                }
            );

            var response = await _httpClient.PostAsync(requestUri, content);

            if (response.IsSuccessStatusCode)
            {
                var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(
                    JsonOptions
                );
                if (tokenResponse is null)
                {
                    return new DevicePollResult
                    {
                        Success = false,
                        Error = "Invalid token response",
                    };
                }

                // Parse scopes from response
                var scopes =
                    tokenResponse.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    ?? Array.Empty<string>();

                var credentials = new NocturneCredentials
                {
                    ApiUrl = state.ApiUrl,
                    AccessToken = tokenResponse.AccessToken,
                    RefreshToken = tokenResponse.RefreshToken ?? string.Empty,
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn),
                    Scopes = scopes.ToList().AsReadOnly(),
                };

                await _credentialStore.SaveCredentialsAsync(credentials);
                await _credentialStore.ClearDeviceAuthStateAsync();

                _logger.LogInformation("Device authorization completed successfully");

                return new DevicePollResult { Success = true };
            }

            var error = await response.Content.ReadFromJsonAsync<OAuthErrorResponse>(JsonOptions);
            var errorCode = error?.Error ?? "unknown_error";

            return errorCode switch
            {
                "authorization_pending" => new DevicePollResult { Success = false, Pending = true },
                "slow_down" => new DevicePollResult
                {
                    Success = false,
                    Pending = true,
                    SlowDown = true,
                },
                "expired_token" => new DevicePollResult
                {
                    Success = false,
                    Expired = true,
                    Error = "Device code has expired",
                },
                "access_denied" => new DevicePollResult
                {
                    Success = false,
                    AccessDenied = true,
                    Error = "User denied the authorization",
                },
                _ => new DevicePollResult
                {
                    Success = false,
                    Error = error?.ErrorDescription ?? errorCode,
                },
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling for device authorization");
            return new DevicePollResult { Success = false, Error = ex.Message };
        }
    }

    /// <inheritdoc />
    public async Task<TokenRefreshResult> RefreshTokenAsync()
    {
        try
        {
            var credentials = await _credentialStore.GetCredentialsAsync();
            if (credentials is null)
            {
                return new TokenRefreshResult
                {
                    Success = false,
                    Error = "No credentials to refresh",
                };
            }

            if (string.IsNullOrEmpty(credentials.RefreshToken))
            {
                return new TokenRefreshResult
                {
                    Success = false,
                    Error = "No refresh token available",
                };
            }

            var requestUri = $"{credentials.ApiUrl.TrimEnd('/')}/api/oauth/token";
            var content = new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = credentials.RefreshToken,
                    ["client_id"] = ClientId,
                }
            );

            var response = await _httpClient.PostAsync(requestUri, content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadFromJsonAsync<OAuthErrorResponse>(
                    JsonOptions
                );
                _logger.LogWarning("Token refresh failed: {Error}", error?.Error);

                // If refresh token is invalid, clear credentials
                if (error?.Error == "invalid_grant")
                {
                    await _credentialStore.DeleteCredentialsAsync();
                }

                return new TokenRefreshResult
                {
                    Success = false,
                    Error = error?.ErrorDescription ?? error?.Error ?? "Token refresh failed",
                };
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(
                JsonOptions
            );
            if (tokenResponse is null)
            {
                return new TokenRefreshResult { Success = false, Error = "Invalid token response" };
            }

            await _credentialStore.UpdateTokensAsync(
                tokenResponse.AccessToken,
                tokenResponse.RefreshToken,
                tokenResponse.ExpiresIn
            );

            _logger.LogInformation("Token refreshed successfully");

            return new TokenRefreshResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing token");
            return new TokenRefreshResult { Success = false, Error = ex.Message };
        }
    }

    /// <inheritdoc />
    public async Task<bool> EnsureValidTokenAsync()
    {
        var credentials = await _credentialStore.GetCredentialsAsync();
        if (credentials is null)
        {
            return false;
        }

        // If token is expired or will expire soon, refresh it
        if (credentials.IsExpired(bufferSeconds: 60))
        {
            _logger.LogDebug("Access token expired or expiring soon, refreshing...");
            var result = await RefreshTokenAsync();
            return result.Success;
        }

        return true;
    }

    /// <inheritdoc />
    public async Task SignOutAsync()
    {
        try
        {
            var credentials = await _credentialStore.GetCredentialsAsync();
            if (credentials != null)
            {
                // Revoke the refresh token
                try
                {
                    var requestUri = $"{credentials.ApiUrl.TrimEnd('/')}/api/oauth/revoke";
                    var content = new FormUrlEncodedContent(
                        new Dictionary<string, string>
                        {
                            ["token"] = credentials.RefreshToken,
                            ["token_type_hint"] = "refresh_token",
                        }
                    );

                    await _httpClient.PostAsync(requestUri, content);
                    _logger.LogDebug("Token revoked successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to revoke token (continuing with sign out)");
                }
            }

            await _credentialStore.DeleteCredentialsAsync();
            await _credentialStore.ClearDeviceAuthStateAsync();

            _logger.LogInformation("Signed out successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during sign out");
            throw;
        }
    }

    #region Response Models

    private class DeviceAuthorizationResponse
    {
        [JsonPropertyName("device_code")]
        public string DeviceCode { get; set; } = string.Empty;

        [JsonPropertyName("user_code")]
        public string UserCode { get; set; } = string.Empty;

        [JsonPropertyName("verification_uri")]
        public string VerificationUri { get; set; } = string.Empty;

        [JsonPropertyName("verification_uri_complete")]
        public string? VerificationUriComplete { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("interval")]
        public int Interval { get; set; } = 5;
    }

    private class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }

    private class OAuthErrorResponse
    {
        [JsonPropertyName("error")]
        public string Error { get; set; } = string.Empty;

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }

    #endregion
}
