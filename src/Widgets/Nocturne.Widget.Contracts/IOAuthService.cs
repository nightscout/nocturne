namespace Nocturne.Widget.Contracts;

/// <summary>
/// Result of initiating the device authorization flow
/// </summary>
public record DeviceAuthorizationResult
{
    /// <summary>Whether the initiation was successful</summary>
    public required bool Success { get; init; }

    /// <summary>Error message if not successful</summary>
    public string? Error { get; init; }

    /// <summary>The device authorization state if successful</summary>
    public DeviceAuthorizationState? State { get; init; }
}

/// <summary>
/// Result of polling for device authorization completion
/// </summary>
public record DevicePollResult
{
    /// <summary>Whether the operation succeeded</summary>
    public required bool Success { get; init; }

    /// <summary>Whether the authorization is still pending (user hasn't approved yet)</summary>
    public bool Pending { get; init; }

    /// <summary>Whether the device code has expired</summary>
    public bool Expired { get; init; }

    /// <summary>Whether the user denied the authorization</summary>
    public bool AccessDenied { get; init; }

    /// <summary>Whether polling should slow down</summary>
    public bool SlowDown { get; init; }

    /// <summary>Error message if failed</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Result of a token refresh operation
/// </summary>
public record TokenRefreshResult
{
    /// <summary>Whether the refresh was successful</summary>
    public required bool Success { get; init; }

    /// <summary>Error message if not successful</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Service for handling OAuth authentication flows for the widget
/// </summary>
public interface IOAuthService
{
    /// <summary>
    /// Initiates the OAuth device authorization flow.
    /// The user must visit the verification URI and enter the user code.
    /// </summary>
    /// <param name="apiUrl">The base URL of the Nocturne server</param>
    /// <param name="scopes">The scopes to request (defaults to read-only glucose access)</param>
    /// <returns>The device authorization result containing the user code and verification URL</returns>
    Task<DeviceAuthorizationResult> InitiateDeviceAuthorizationAsync(
        string apiUrl,
        IEnumerable<string>? scopes = null
    );

    /// <summary>
    /// Polls for device authorization completion.
    /// Should be called at the interval specified in the device authorization response.
    /// </summary>
    /// <returns>The poll result indicating success, pending, or failure</returns>
    Task<DevicePollResult> PollForAuthorizationAsync();

    /// <summary>
    /// Refreshes the access token using the stored refresh token.
    /// Should be called before the access token expires.
    /// </summary>
    /// <returns>The refresh result</returns>
    Task<TokenRefreshResult> RefreshTokenAsync();

    /// <summary>
    /// Ensures we have a valid access token, refreshing if necessary.
    /// </summary>
    /// <returns>True if we have valid credentials, false otherwise</returns>
    Task<bool> EnsureValidTokenAsync();

    /// <summary>
    /// Signs out by revoking the tokens and clearing credentials.
    /// </summary>
    Task SignOutAsync();
}
