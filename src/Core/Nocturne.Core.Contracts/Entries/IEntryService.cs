using System.Text.Json;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Entries;

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
    Task<IEnumerable<Entry>> GetEntriesAsync(
        string? type,
        int count,
        int skip,
        CancellationToken cancellationToken
    );

    /// <returns>Entry if found, null otherwise</returns>
    Task<Entry?> GetEntryByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find the stored entry an upload would duplicate: one of the same type, from the same device,
    /// at the same millisecond.
    /// </summary>
    /// <remarks>
    /// A duplicate has the same type, device and millisecond, whatever its value. Nightscout keys an
    /// entry on its exact time and type, and uploaders post one reading at a time, so on a flat
    /// trace consecutive readings carry the same value and any window would discard real ones.
    /// </remarks>
    /// <param name="device">Device identifier to match, or <c>null</c> to match any device.</param>
    /// <param name="type">Entry type (e.g., "sgv", "mbg", "cal")</param>
    /// <param name="mills">Timestamp in milliseconds since Unix epoch</param>
    /// <returns>Existing entry if duplicate found, null otherwise</returns>
    Task<Entry?> CheckForDuplicateEntryAsync(
        string? device,
        string type,
        long mills,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Writes a processed v1 upload batch and says what to echo and which entries moved glucose.
    /// </summary>
    /// <remarks>
    /// Uses one duplicate query per entry type for the whole batch instead of one per entry, with the
    /// same per-entry classification as
    /// <see cref="CheckForDuplicateEntryAsync(string?, string, long, CancellationToken)"/>.
    /// A re-send of a stored reading updates it, as Nightscout's upsert on <c>{sysTime, type}</c>
    /// does: xDrip+ re-sends the reading paired with a calibration at its original time, with no
    /// <c>_id</c> and the recalculated value. Like that upsert's <c>$set</c>, it changes only the
    /// fields the re-send carries. It is written under the stored row's legacy id, so the legacy-id
    /// upsert updates that row and its material-change gate keeps an identical re-send silent. A
    /// stored row with no legacy id is out of that upsert's reach, so a re-send of it is echoed as
    /// stored and not written.
    /// Every re-send is echoed: v1 uploaders (Loop's NightscoutKit) treat a response shorter than the
    /// upload as a failed upload and retry the batch forever.
    /// </remarks>
    /// <param name="processed">The batch after document processing, in submission order.</param>
    /// <param name="uploader">The uploading client's User-Agent, named in the log line that reports a
    /// client re-sending its stored backlog.</param>
    Task<EntryUploadResult> UpsertUploadAsync(
        IReadOnlyList<Entry> processed,
        string? uploader,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Create new entries with WebSocket broadcasting
    /// </summary>
    /// <param name="origin">
    /// Write origin for the decomposed records. <see cref="WriteOrigin.Backfill"/> suppresses
    /// the real-time broadcast so a historical import doesn't flood connected clients.
    /// </param>
    /// <returns>
    /// The accepted entries, carrying how many of their records were not written because the user
    /// had deleted them.
    /// </returns>
    Task<BulkWrite<Entry>> CreateEntriesAsync(
        IEnumerable<Entry> entries,
        WriteOrigin origin = WriteOrigin.Live,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Writes a page of entries imported from another Nightscout as a backfill, with no real-time
    /// broadcast.
    /// </summary>
    /// <returns>What the page stored, and what it passed over as deleted or unsupported.</returns>
    Task<DecompositionResult> ImportEntriesAsync(
        IReadOnlyList<Entry> entries,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update an existing entry with WebSocket broadcasting
    /// </summary>
    /// <param name="entry">Updated entry data</param>
    /// <returns>Updated entry if successful, null otherwise</returns>
    Task<Entry?> UpdateEntryAsync(
        string id,
        Entry entry,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Partially update an entry via JSON merge-patch.
    /// </summary>
    /// <remarks>
    /// AAPS's NSClientV3 uses <c>PATCH v3/entries/{id}</c> rather than <c>PUT</c> to update entries;
    /// see <see cref="Treatments.ITreatmentService.PatchTreatmentAsync"/> for the equivalent treatments implementation
    /// this mirrors.
    /// </remarks>
    /// <param name="patchData">JSON merge-patch data</param>
    /// <returns>Patched entry if successful, null otherwise</returns>
    Task<Entry?> PatchEntryAsync(
        string id,
        JsonElement patchData,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete an entry with WebSocket broadcasting
    /// </summary>
    /// <returns>True if deleted successfully, false otherwise</returns>
    Task<bool> DeleteEntryAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete multiple entries with optional filtering
    /// </summary>
    /// <param name="find">Optional MongoDB query filter</param>
    /// <returns>Number of entries deleted</returns>
    Task<long> DeleteEntriesAsync(
        string? find = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get the most recent entry (current entry)
    /// </summary>
    /// <returns>Most recent entry if exists, null otherwise</returns>
    Task<Entry?> GetCurrentEntryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Entries whose <c>srvModified</c> falls after <paramref name="cursorMills"/>, oldest
    /// modification first, for the v3 <c>history/{lastModified}</c> endpoint. Readings a regular
    /// read hides (demo rows, losing canonical streams) are withheld but still advance the cursor.
    /// </summary>
    /// <param name="cursorMills">The client's cursor, in Unix milliseconds.</param>
    /// <param name="limit">The page size; a page may exceed it to finish its last millisecond.</param>
    Task<ModifiedSincePage<Entry>> GetEntriesModifiedSinceAsync(
        long cursorMills,
        int limit,
        CancellationToken cancellationToken = default
    );

    /// <summary>Entries matching <paramref name="query"/>, in the order it asks for.</summary>
    Task<IEnumerable<Entry>> QueryEntriesAsync(EntryQuery query, CancellationToken cancellationToken = default);

    /// <summary>Counts entries matching the optional filter and type.</summary>
    /// <param name="find">Optional MongoDB-style find query for time-range extraction.</param>
    /// <param name="type">Optional entry type filter ("sgv", "mbg", "cal").</param>
    Task<long> CountEntriesAsync(
        string? find = null,
        string? type = null,
        CancellationToken cancellationToken = default
    );
}
