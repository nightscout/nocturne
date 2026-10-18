using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Repositories;

/// <summary>
/// Repository port for <see cref="Settings"/> domain operations.
/// </summary>
/// <seealso cref="Settings"/>
public interface ISettingsRepository
{
    /// <summary>
    /// Get all settings entries
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of all settings entries</returns>
    Task<IEnumerable<Settings>> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get settings entries with advanced filtering options
    /// </summary>
    /// <param name="count">Maximum number of settings entries to return</param>
    /// <param name="skip">Number of settings entries to skip</param>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="reverseResults">Whether to reverse the order of results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of settings entries matching the filter</returns>
    Task<IEnumerable<Settings>> GetSettingsWithAdvancedFilterAsync(
        int count = 10,
        int skip = 0,
        string? findQuery = null,
        bool reverseResults = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get a settings entry by its ID
    /// </summary>
    /// <param name="id">The settings ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The settings entry if found, null otherwise</returns>
    Task<Settings?> GetSettingsByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a settings entry by its key
    /// </summary>
    /// <param name="key">The settings key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The settings entry if found, null otherwise</returns>
    Task<Settings?> GetSettingsByKeyAsync(
        string key,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create multiple settings entries
    /// </summary>
    /// <param name="settings">The settings entries to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of created settings entries</returns>
    Task<IEnumerable<Settings>> CreateSettingsAsync(
        IEnumerable<Settings> settings,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update a settings entry by ID
    /// </summary>
    /// <param name="id">The settings ID</param>
    /// <param name="settings">The updated settings data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated settings entry, or null if not found</returns>
    Task<Settings?> UpdateSettingsAsync(
        string id,
        Settings settings,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a settings entry by ID
    /// </summary>
    /// <param name="id">The settings ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteSettingsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count settings entries with optional filtering
    /// </summary>
    /// <param name="findQuery">Optional query filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of settings entries matching the filter</returns>
    Task<long> CountSettingsAsync(
        string? findQuery = null,
        CancellationToken cancellationToken = default
    );
}
