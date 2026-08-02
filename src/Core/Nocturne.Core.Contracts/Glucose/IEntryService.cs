using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Glucose;

/// <summary>
/// Domain service for <see cref="Entry"/> operations with WebSocket broadcasting.
/// </summary>
/// <remarks>
/// Entries represent continuous glucose monitor (CGM) readings, meter blood glucose checks, and calibrations.
/// The <see cref="Entry.Mills"/> field (Unix milliseconds) is the source-of-truth timestamp;
/// <see cref="Entry.Date"/> and <c>DateString</c> are computed from it.
/// </remarks>
/// <seealso cref="Entry"/>
public interface IEntryService
{
    /// <summary>
    /// Get entries with optional filtering and pagination
    /// </summary>
    /// <param name="find">Optional MongoDB query filter</param>
    /// <param name="count">Maximum number of entries to return</param>
    /// <param name="skip">Number of entries to skip for pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of entries</returns>
    Task<IEnumerable<Entry>> GetEntriesAsync(
        string? find = null,
        int? count = null,
        int? skip = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get entries with type filtering and pagination
    /// </summary>
    /// <param name="type">Entry type filter (e.g., "sgv", "mbg", "cal")</param>
    /// <param name="count">Maximum number of entries to return</param>
    /// <param name="skip">Number of entries to skip for pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of entries</returns>
    Task<IEnumerable<Entry>> GetEntriesAsync(
        string? type,
        int count,
        int skip,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Get a specific entry by ID
    /// </summary>
    /// <param name="id">Entry ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Entry if found, null otherwise</returns>
    Task<Entry?> GetEntryByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check for duplicate entries in the database within a time window
    /// </summary>
    /// <param name="device">Device identifier</param>
    /// <param name="type">Entry type (e.g., "sgv", "mbg", "cal")</param>
    /// <param name="sgv">Sensor glucose value in mg/dL</param>
    /// <param name="mills">Timestamp in milliseconds since Unix epoch</param>
    /// <param name="windowMinutes">Time window in minutes to check for duplicates (default: 5)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Existing entry if duplicate found, null otherwise</returns>
    Task<Entry?> CheckForDuplicateEntryAsync(
        string? device,
        string type,
        double? sgv,
        long mills,
        int windowMinutes = 5,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create new entries with WebSocket broadcasting
    /// </summary>
    /// <param name="entries">Entries to create</param>
    /// <param name="origin">
    /// Write origin for the decomposed records. <see cref="WriteOrigin.Backfill"/> suppresses
    /// the real-time broadcast so a historical import doesn't flood connected clients.
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created entries with assigned IDs</returns>
    Task<IEnumerable<Entry>> CreateEntriesAsync(
        IEnumerable<Entry> entries,
        WriteOrigin origin = WriteOrigin.Live,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update an existing entry with WebSocket broadcasting
    /// </summary>
    /// <param name="id">Entry ID to update</param>
    /// <param name="entry">Updated entry data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated entry if successful, null otherwise</returns>
    Task<Entry?> UpdateEntryAsync(
        string id,
        Entry entry,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete an entry with WebSocket broadcasting
    /// </summary>
    /// <param name="id">Entry ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteEntryAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete multiple entries with optional filtering
    /// </summary>
    /// <param name="find">Optional MongoDB query filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of entries deleted</returns>
    Task<long> DeleteEntriesAsync(
        string? find = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get the most recent entry (current entry)
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Most recent entry if exists, null otherwise</returns>
    Task<Entry?> GetCurrentEntryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get entries with advanced filtering capabilities
    /// </summary>
    /// <param name="find">MongoDB query filter</param>
    /// <param name="count">Maximum number of entries to return</param>
    /// <param name="skip">Number of entries to skip for pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of entries matching the filter</returns>
    Task<IEnumerable<Entry>> GetEntriesWithAdvancedFilterAsync(
        string find,
        int count,
        int skip,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get entries with advanced filtering capabilities including type, date, and sorting
    /// </summary>
    /// <param name="type">Entry type filter (e.g., "sgv", "mbg", "cal")</param>
    /// <param name="count">Maximum number of entries to return</param>
    /// <param name="skip">Number of entries to skip for pagination</param>
    /// <param name="findQuery">MongoDB query filter</param>
    /// <param name="dateString">Date filter string</param>
    /// <param name="reverseResults">Whether to reverse the result order</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of entries matching the filter</returns>
    Task<IEnumerable<Entry>> GetEntriesWithAdvancedFilterAsync(
        string? type,
        int count,
        int skip,
        string? findQuery,
        string? dateString,
        bool reverseResults,
        CancellationToken cancellationToken = default
    );
}
