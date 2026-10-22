using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Repositories;

/// <summary>
/// Repository port for <see cref="Settings"/> domain operations.
/// </summary>
/// <seealso cref="Settings"/>
public interface ISettingsRepository
{
    /// <returns>Collection of all settings entries</returns>
    Task<IEnumerable<Settings>> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get settings entries with advanced filtering options
    /// </summary>
    /// <param name="count">Maximum number of settings entries to return</param>
    /// <param name="skip">Number of settings entries to skip</param>
    /// <param name="reverseResults">Whether to reverse the order of results</param>
    /// <returns>Collection of settings entries matching the filter</returns>
    Task<IEnumerable<Settings>> GetSettingsWithAdvancedFilterAsync(
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    );

    /// <returns>The settings entry if found, null otherwise</returns>
    Task<Settings?> GetSettingsByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <returns>The settings entry if found, null otherwise</returns>
    Task<Settings?> GetSettingsByKeyAsync(
        string key,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create multiple settings entries
    /// </summary>
    /// <param name="settings">The settings entries to create</param>
    /// <returns>Collection of created settings entries</returns>
    Task<IEnumerable<Settings>> CreateSettingsAsync(
        IEnumerable<Settings> settings,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Store <paramref name="settings"/> as the document <paramref name="id"/> addresses: replace
    /// it when one is stored, keeping its key and <c>srvCreated</c>, or insert it under
    /// <paramref name="id"/> as its key. The v3 UPDATE (PUT) semantics.
    /// </summary>
    /// <param name="id">A legacy id, row GUID, or key; a new document takes it as its key</param>
    /// <param name="settings">The whole document, with <c>srvModified</c> set</param>
    /// <returns>True when the document was inserted, false when it replaced a stored one</returns>
    Task<bool> PutSettingsAsync(
        string id,
        Settings settings,
        CancellationToken cancellationToken = default
    );

    /// <param name="settings">The updated settings data</param>
    /// <returns>The updated settings entry, or null if not found</returns>
    Task<Settings?> UpdateSettingsAsync(
        string id,
        Settings settings,
        CancellationToken cancellationToken = default
    );

    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteSettingsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count settings entries with optional filtering
    /// </summary>
    /// <param name="findQuery">Optional query filter</param>
    /// <returns>Number of settings entries matching the filter</returns>
    Task<long> CountSettingsAsync(
        string? findQuery = null,
        CancellationToken cancellationToken = default
    );
}
