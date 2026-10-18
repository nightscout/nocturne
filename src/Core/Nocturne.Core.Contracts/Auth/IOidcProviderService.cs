using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Service for managing OIDC provider configurations
/// </summary>
public interface IOidcProviderService
{
    /// <summary>
    /// Returns true when OIDC providers are defined in config (Oidc:Providers[]).
    /// When true, the database is bypassed and the management UI should be hidden.
    /// </summary>
    bool IsConfigManaged { get; }

    /// <returns>List of enabled providers ordered by display order</returns>
    Task<List<OidcProvider>> GetEnabledProvidersAsync();

    /// <summary>
    /// Get all OIDC providers (including disabled)
    /// </summary>
    Task<List<OidcProvider>> GetAllProvidersAsync();

    /// <returns>Provider if found, null otherwise</returns>
    Task<OidcProvider?> GetProviderByIdAsync(Guid providerId);

    /// <returns>Provider if found, null otherwise</returns>
    Task<OidcProvider?> GetProviderByIssuerAsync(string issuerUrl);

    /// <param name="provider">Provider configuration</param>
    Task<OidcProvider> CreateProviderAsync(OidcProvider provider);

    /// <param name="provider">Provider configuration</param>
    /// <returns>Updated provider or null if not found</returns>
    Task<OidcProvider?> UpdateProviderAsync(OidcProvider provider);

    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteProviderAsync(Guid providerId);

    /// <returns>True if enabled, false if not found</returns>
    Task<bool> EnableProviderAsync(Guid providerId);

    /// <returns>True if disabled, false if not found</returns>
    Task<bool> DisableProviderAsync(Guid providerId);

    /// <summary>
    /// Fetch and cache the OIDC discovery document for a provider
    /// </summary>
    /// <param name="forceRefresh">Force refresh even if cached</param>
    /// <returns>Discovery document if successful, null otherwise</returns>
    Task<OidcDiscoveryDocument?> GetDiscoveryDocumentAsync(Guid providerId, bool forceRefresh = false);

    Task<OidcProviderTestResult> TestProviderAsync(Guid providerId);
}

/// <summary>
/// Result of testing OIDC provider connectivity
/// </summary>
public class OidcProviderTestResult
{
    public bool Success { get; set; }

    /// <summary>
    /// Error message if test failed
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Discovery document if retrieved
    /// </summary>
    public OidcDiscoveryDocument? DiscoveryDocument { get; set; }

    /// <summary>
    /// Time taken to fetch discovery document
    /// </summary>
    public TimeSpan ResponseTime { get; set; }

    /// <summary>
    /// Warnings (non-fatal issues)
    /// </summary>
    public List<string> Warnings { get; set; } = new();
}
