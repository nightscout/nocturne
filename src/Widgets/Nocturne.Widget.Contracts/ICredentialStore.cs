namespace Nocturne.Widget.Contracts;

/// <summary>
/// OAuth credentials for connecting to a Nocturne instance
/// </summary>
public record NocturneCredentials
{
    /// <summary>The base URL of the Nocturne API</summary>
    public required string ApiUrl { get; init; }

    /// <summary>The OAuth access token (JWT)</summary>
    public required string AccessToken { get; init; }

    /// <summary>The OAuth refresh token for obtaining new access tokens</summary>
    public required string RefreshToken { get; init; }

    /// <summary>When the access token expires (UTC)</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The scopes granted to this token</summary>
    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>
    /// Returns true if the access token has expired or will expire within the buffer period
    /// </summary>
    /// <param name="bufferSeconds">Buffer time before expiration to consider expired (default 60s)</param>
    public bool IsExpired(int bufferSeconds = 60) =>
        DateTimeOffset.UtcNow >= ExpiresAt.AddSeconds(-bufferSeconds);
}

/// <summary>
/// Pending device authorization state during the OAuth device flow
/// </summary>
public record DeviceAuthorizationState
{
    /// <summary>The base URL of the Nocturne API</summary>
    public required string ApiUrl { get; init; }

    /// <summary>The device code to poll with</summary>
    public required string DeviceCode { get; init; }

    /// <summary>The user code to display to the user</summary>
    public required string UserCode { get; init; }

    /// <summary>The verification URI for the user to visit</summary>
    public required string VerificationUri { get; init; }

    /// <summary>Optional: complete verification URI with user code</summary>
    public string? VerificationUriComplete { get; init; }

    /// <summary>When the device code expires (UTC)</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Minimum polling interval in seconds</summary>
    public required int Interval { get; init; }
}

/// <summary>
/// Interface for securely storing and retrieving Nocturne API credentials
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Retrieves the stored OAuth credentials
    /// </summary>
    /// <returns>The stored credentials or null if none exist</returns>
    Task<NocturneCredentials?> GetCredentialsAsync();

    /// <summary>
    /// Saves OAuth credentials to the secure store
    /// </summary>
    /// <param name="credentials">The credentials to store</param>
    Task SaveCredentialsAsync(NocturneCredentials credentials);

    /// <summary>
    /// Updates the credentials with new tokens from a refresh operation
    /// </summary>
    /// <param name="accessToken">The new access token</param>
    /// <param name="refreshToken">The new refresh token (may be the same)</param>
    /// <param name="expiresIn">Seconds until the access token expires</param>
    Task UpdateTokensAsync(string accessToken, string? refreshToken, int expiresIn);

    /// <summary>
    /// Deletes the stored credentials
    /// </summary>
    Task DeleteCredentialsAsync();

    /// <summary>
    /// Checks if credentials are stored
    /// </summary>
    /// <returns>True if credentials exist, false otherwise</returns>
    Task<bool> HasCredentialsAsync();

    /// <summary>
    /// Saves the pending device authorization state during the device flow
    /// </summary>
    Task SaveDeviceAuthStateAsync(DeviceAuthorizationState state);

    /// <summary>
    /// Gets the pending device authorization state
    /// </summary>
    Task<DeviceAuthorizationState?> GetDeviceAuthStateAsync();

    /// <summary>
    /// Clears the pending device authorization state
    /// </summary>
    Task ClearDeviceAuthStateAsync();
}
