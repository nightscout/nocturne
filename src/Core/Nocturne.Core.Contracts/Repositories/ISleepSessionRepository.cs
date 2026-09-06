using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Repositories;

/// <summary>
/// Repository port for <see cref="SleepSession"/> operations. Sleep sessions represent
/// time-bounded sleep periods recorded by wearables or health platforms.
/// </summary>
/// <seealso cref="SleepSession"/>
/// <seealso cref="SleepSessionType"/>
/// <seealso cref="SleepSource"/>
public interface ISleepSessionRepository
{
    /// <summary>
    /// Queries sleep sessions with optional filtering by time range, type, and source.
    /// </summary>
    /// <param name="from">Optional start of the time range (inclusive).</param>
    /// <param name="to">Optional end of the time range (inclusive).</param>
    /// <param name="type">Optional <see cref="SleepSessionType"/> filter.</param>
    /// <param name="source">Optional <see cref="SleepSource"/> filter.</param>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="offset">Number of records to skip for pagination.</param>
    /// <param name="descending">When <c>true</c>, orders by start time descending.</param>
    /// <param name="includeStages">When <c>true</c>, populates <see cref="SleepSession.Stages"/> on each record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A collection of matching <see cref="SleepSession"/> records.</returns>
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
    /// <param name="from">Optional start of the time range (inclusive).</param>
    /// <param name="to">Optional end of the time range (inclusive).</param>
    /// <param name="type">Optional <see cref="SleepSessionType"/> filter.</param>
    /// <param name="source">Optional <see cref="SleepSource"/> filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total number of matching records.</returns>
    Task<int> CountSessionsAsync(
        DateTime? from = null,
        DateTime? to = null,
        SleepSessionType? type = null,
        SleepSource? source = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a single sleep session by its identifier.
    /// </summary>
    /// <param name="id">The sleep session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The <see cref="SleepSession"/> if found, or <c>null</c>.</returns>
    Task<SleepSession?> GetSessionByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a sleep session, matched by its identifier or original ID.
    /// </summary>
    /// <param name="session">The <see cref="SleepSession"/> to upsert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted <see cref="SleepSession"/>.</returns>
    Task<SleepSession> UpsertSessionAsync(
        SleepSession session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing sleep session by ID.
    /// </summary>
    /// <param name="id">The sleep session identifier.</param>
    /// <param name="session">The updated <see cref="SleepSession"/> data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated <see cref="SleepSession"/>, or <c>null</c> if not found.</returns>
    Task<SleepSession?> UpdateSessionAsync(
        Guid id,
        SleepSession session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a sleep session by ID.
    /// </summary>
    /// <param name="id">The sleep session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if deleted; <c>false</c> if not found.</returns>
    Task<bool> DeleteSessionAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
