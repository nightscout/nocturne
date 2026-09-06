using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Sleep;

/// <summary>
/// Domain service for <see cref="SleepSession"/> lifecycle operations.
/// Sleep sessions represent time-bounded sleep periods recorded by wearables
/// or health platforms.
/// </summary>
/// <seealso cref="SleepSession"/>
/// <seealso cref="SleepSessionType"/>
/// <seealso cref="SleepSource"/>
/// <seealso cref="Nocturne.Core.Contracts.Repositories.ISleepSessionRepository"/>
public interface ISleepService
{
    /// <summary>
    /// Queries sleep sessions with optional filtering by time range, type, and source.
    /// </summary>
    Task<IEnumerable<SleepSession>> GetSessionsAsync(
        DateTime? from = null,
        DateTime? to = null,
        SleepSessionType? type = null,
        SleepSource? source = null,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        bool includeStages = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts sleep sessions matching the specified filters.
    /// </summary>
    Task<int> CountSessionsAsync(
        DateTime? from = null,
        DateTime? to = null,
        SleepSessionType? type = null,
        SleepSource? source = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a single sleep session by its identifier.
    /// </summary>
    Task<SleepSession?> GetSessionByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a sleep session, matched by its identifier or original ID.
    /// </summary>
    Task<SleepSession> UpsertSessionAsync(SleepSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing sleep session by ID.
    /// </summary>
    Task<SleepSession?> UpdateSessionAsync(Guid id, SleepSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a sleep session by ID.
    /// </summary>
    Task<bool> DeleteSessionAsync(Guid id, CancellationToken cancellationToken = default);
}
