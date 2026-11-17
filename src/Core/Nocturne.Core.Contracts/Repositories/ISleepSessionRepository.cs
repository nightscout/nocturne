using Nocturne.Core.Contracts.Audit;
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
    /// <returns>The total number of matching records.</returns>
    Task<int> CountSessionsAsync(
        DateTime? from = null,
        DateTime? to = null,
        SleepSessionType? type = null,
        SleepSource? source = null,
        CancellationToken cancellationToken = default);

    /// <returns>The <see cref="SleepSession"/> if found, or <c>null</c>.</returns>
    Task<SleepSession?> GetSessionByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The live session stored under the (<paramref name="source"/>, <paramref name="originalId"/>)
    /// key <see cref="UpsertSessionAsync"/> deduplicates on.
    /// </summary>
    /// <returns>The <see cref="SleepSession"/> if found, or <c>null</c>.</returns>
    Task<SleepSession?> GetSessionBySourceRecordAsync(
        SleepSource source,
        string originalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a sleep session, matched by its identifier or original ID.
    /// </summary>
    /// <param name="session">The <see cref="SleepSession"/> to upsert.</param>
    /// <returns>The persisted <see cref="SleepSession"/>.</returns>
    /// <exception cref="V4.Repositories.RecreationBlockedException">
    /// The session matches one the user deleted, which a re-upload never brings back.
    /// </exception>
    Task<SleepSession> UpsertSessionAsync(
        SleepSession session,
        CancellationToken cancellationToken = default);

    /// <param name="session">The updated <see cref="SleepSession"/> data.</param>
    /// <returns>The updated <see cref="SleepSession"/>, or <c>null</c> if not found.</returns>
    /// <exception cref="V4.Repositories.RecreationBlockedException">
    /// The update moves the session onto a source and original ID that another live session, or one
    /// the user deleted, holds.
    /// </exception>
    Task<SleepSession?> UpdateSessionAsync(
        Guid id,
        SleepSession session,
        CancellationToken cancellationToken = default);

    /// <returns><c>true</c> if deleted; <c>false</c> if not found.</returns>
    Task<bool> DeleteSessionAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-deletes every session <paramref name="sourceApp"/> recorded through
    /// <paramref name="source"/>, user tombstones included, with their stages and samples. Returns the
    /// number deleted.
    /// </summary>
    Task<int> PurgeSourceAppAsync(
        SleepSource source, string sourceApp, CancellationToken cancellationToken = default);

    /// <summary>
    /// The live sessions <paramref name="sourceApp"/> recorded through <paramref name="source"/> that
    /// carry an original id and ended in <c>[from, to)</c>, at most <paramref name="limit"/> of them, in
    /// no order. The key is the original id.
    /// </summary>
    Task<IReadOnlyList<SourceRecordKey>> GetSourceAppKeysAsync(
        SleepSource source, string sourceApp, DateTime from, DateTime to, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes the live sessions of <paramref name="ids"/> that <paramref name="sourceApp"/>
    /// recorded through <paramref name="source"/>, as a system delete that a later sync may re-create.
    /// Returns the number deleted.
    /// </summary>
    Task<int> RetireAsync(
        SleepSource source, string sourceApp, IReadOnlyCollection<Guid> ids, DateTime deletedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the sessions <paramref name="sourceApp"/> recorded through <paramref name="source"/>
    /// starting from <paramref name="since"/> on, or all of them when it is null. Returns null when
    /// there are none.
    /// </summary>
    Task<SleepSessionStats?> DescribeSourceAppAsync(
        SleepSource source, string sourceApp, DateTime? since, DateTime last24Hours,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes the live sessions <paramref name="sourceApp"/> recorded through
    /// <paramref name="source"/> for <paramref name="auditContext"/>, recording one summary row for
    /// <paramref name="scope"/>. Returns the number deleted.
    /// </summary>
    Task<int> DeleteSourceAppAsync(
        SleepSource source, string sourceApp, IAuditContext auditContext, string scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the user deletions among the sessions <paramref name="sourceApp"/> recorded through
    /// <paramref name="source"/>, so that a sync can import them again, recording one summary row for
    /// <paramref name="scope"/>. The sessions stay deleted. Returns the number released.
    /// </summary>
    Task<int> AllowSourceAppReimportAsync(
        SleepSource source, string sourceApp, IAuditContext auditContext, string scope,
        CancellationToken cancellationToken = default);
}

/// <summary>How many sessions a source recorded, and over what span.</summary>
/// <param name="Count">The sessions counted.</param>
/// <param name="CountLast24Hours">Those that started in the last 24 hours.</param>
/// <param name="Latest">The latest start.</param>
/// <param name="Oldest">The oldest start.</param>
public sealed record SleepSessionStats(long Count, int CountLast24Hours, DateTime Latest, DateTime Oldest);
