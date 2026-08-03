using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Connectors.Core.Interfaces;

public interface IMetadataPublisher
{
    Task<bool> PublishProfilesAsync(
        IEnumerable<Profile> profiles,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishFoodAsync(
        IEnumerable<Food> foods,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConnectorFoodEntry>?> PublishConnectorFoodEntriesAsync(
        IEnumerable<ConnectorFoodEntryImport> entries,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Withdraws pending food entries in <paramref name="from"/>..<paramref name="to"/> that
    /// <paramref name="presentExternalEntryIds"/> does not mention. Call only after reading the source
    /// exhaustively: absence from a partial read is not evidence of a deletion, and an incomplete list
    /// withdraws every entry it omitted.
    /// </summary>
    /// <returns>The number of entries withdrawn, or <c>null</c> if reconciliation failed.</returns>
    Task<int?> ReconcileConnectorFoodEntriesAsync(
        IEnumerable<string> presentExternalEntryIds,
        DateTimeOffset from,
        DateTimeOffset to,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishActivityAsync(
        IEnumerable<Activity> activities,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishStateSpansAsync(
        IEnumerable<StateSpan> stateSpans,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishSystemEventsAsync(
        IEnumerable<SystemEvent> systemEvents,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishNotesAsync(
        IEnumerable<Note> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the timestamp of the most recent activity record for the current tenant,
    /// used by connectors to resume catch-up from where they left off, or <c>null</c> if none exist.
    /// </summary>
    Task<DateTime?> GetLatestActivityTimestampAsync(
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the persisted backfill low-water mark for one of <paramref name="source"/>'s data
    /// collections. A mark means an earlier backfill crawl of that collection stopped before
    /// reaching the source's beginning — history older than the mark may be missing, and the
    /// connector should resume the crawl below it. <c>null</c> means no incomplete backfill.
    /// </summary>
    /// <param name="source">The connector data source (e.g. <c>nightscout-connector</c>).</param>
    /// <param name="collection">The data collection key (e.g. <c>Glucose</c>, <c>Treatments</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DateTime?> GetBackfillLowWaterMarkAsync(
        string source,
        string collection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists (or clears, with <c>null</c>) the backfill low-water mark for one of
    /// <paramref name="source"/>'s data collections. Connectors update the mark as each page of
    /// a newest-first backfill lands, so a crawl killed mid-run (process restart, publish
    /// failure) can resume from where it stopped instead of stranding the older history below
    /// the resume cursor forever.
    /// </summary>
    /// <param name="source">The connector data source (e.g. <c>nightscout-connector</c>).</param>
    /// <param name="collection">The data collection key (e.g. <c>Glucose</c>, <c>Treatments</c>).</param>
    /// <param name="lowWaterMark">The oldest successfully published record time, or <c>null</c> once the crawl reaches the source's beginning.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetBackfillLowWaterMarkAsync(
        string source,
        string collection,
        DateTime? lowWaterMark,
        CancellationToken cancellationToken = default);
}
