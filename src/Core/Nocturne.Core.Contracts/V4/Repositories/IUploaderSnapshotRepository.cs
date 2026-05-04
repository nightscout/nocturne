using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="UploaderSnapshot"/> records that capture the state reported by an uploader
/// application (e.g., xDrip+, Nightscout Uploader) at a point in time.
/// </summary>
/// <remarks>
/// Uploader snapshots include battery level, network status, and version information reported by
/// the phone or device running the CGM uploader. They are distinct from <see cref="PumpSnapshot"/>
/// records, which capture pump hardware state.
/// </remarks>
/// <seealso cref="UploaderSnapshot"/>
/// <seealso cref="IPumpSnapshotRepository"/>
/// <seealso cref="IV4Repository{T}"/>
public interface IUploaderSnapshotRepository : IV4Repository<UploaderSnapshot>
{
    /// <summary>Retrieve a page of <see cref="UploaderSnapshot"/> records filtered by time range, device, and source.</summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<IEnumerable<UploaderSnapshot>> GetAsync(DateTime? from, DateTime? to, string? device, string? source, int limit = 100, int offset = 0, bool descending = true, CancellationToken ct = default);

    /// <summary>Returns a single <see cref="UploaderSnapshot"/> by its UUID v7, or <c>null</c> if not found.</summary>
    /// <param name="id">UUID v7 record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<UploaderSnapshot?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Retrieve an <see cref="UploaderSnapshot"/> by its original MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching record, or <c>null</c> if not found.</returns>
    Task<UploaderSnapshot?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Persist a new <see cref="UploaderSnapshot"/> and return the saved entity.</summary>
    /// <param name="model">Record to create.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<UploaderSnapshot> CreateAsync(UploaderSnapshot model, CancellationToken ct = default);

    /// <summary>Replace an existing <see cref="UploaderSnapshot"/> identified by <paramref name="id"/>.</summary>
    /// <param name="id">UUID v7 identifier of the record to update.</param>
    /// <param name="model">Updated record data.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<UploaderSnapshot> UpdateAsync(Guid id, UploaderSnapshot model, CancellationToken ct = default);

    /// <summary>Delete an <see cref="UploaderSnapshot"/> by its UUID v7.</summary>
    /// <param name="id">UUID v7 identifier of the record to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Delete the <see cref="UploaderSnapshot"/> with the given legacy MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted (0 or 1).</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Retrieve <see cref="UploaderSnapshot"/> records matching any of the given correlation IDs.</summary>
    /// <param name="correlationIds">Correlation IDs to match.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<UploaderSnapshot>> GetByCorrelationIdsAsync(IEnumerable<Guid> correlationIds, CancellationToken ct = default);

    /// <summary>Count <see cref="UploaderSnapshot"/> records within an optional time range.</summary>
    /// <param name="from">Inclusive start, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end, or <c>null</c> for no upper bound.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default);

    /// <summary>
    /// Returns the <see cref="UploaderSnapshot"/> representing the weakest uploader for the
    /// current tenant — i.e. the row with the lowest <see cref="UploaderSnapshot.Battery"/>
    /// among the most recent telemetry — or <c>null</c> if none exists.
    /// </summary>
    /// <remarks>
    /// When multiple uploaders report telemetry, returns the one with the lowest battery so
    /// alerts reflect the weakest device. Rows with <c>Battery = null</c> sort last; ties
    /// break by most-recent <c>Timestamp</c>.
    /// </remarks>
    /// <param name="asOf">When non-null, restricts to snapshots with <c>Timestamp &lt;= asOf</c>;
    /// when <c>null</c>, returns the absolute latest snapshot per the lowest-battery rule.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<UploaderSnapshot?> GetLatestAsync(DateTime? asOf, CancellationToken ct = default);
}
