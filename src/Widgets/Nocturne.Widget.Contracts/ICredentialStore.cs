namespace Nocturne.Widget.Contracts;

/// <summary>
/// Credentials for connecting to a Nocturne instance
/// </summary>
/// <param name="ApiUrl">The base URL of the Nocturne API</param>
/// <param name="Token">The authentication token (API secret or JWT)</param>
public record NocturneCredentials(string ApiUrl, string Token);

/// <summary>
/// Interface for securely storing and retrieving Nocturne API credentials
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Retrieves the stored credentials
    /// </summary>
    /// <returns>The stored credentials or null if none exist</returns>
    Task<NocturneCredentials?> GetCredentialsAsync();

    /// <summary>
    /// Saves credentials to the secure store
    /// </summary>
    /// <param name="credentials">The credentials to store</param>
    Task SaveCredentialsAsync(NocturneCredentials credentials);

    /// <summary>
    /// Deletes the stored credentials
    /// </summary>
    Task DeleteCredentialsAsync();

    /// <summary>
    /// Checks if credentials are stored
    /// </summary>
    /// <returns>True if credentials exist, false otherwise</returns>
    Task<bool> HasCredentialsAsync();
}
