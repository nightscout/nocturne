using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="PumpSnapshot"/> records that capture the complete state of an insulin pump
/// at a moment in time (reservoir level, active insulin, cartridge info, etc.).
/// </summary>
/// <remarks>
/// Pump snapshots are typically produced by the uploader or connector on each sync cycle.
/// They differ from <see cref="ApsSnapshot"/> records, which capture loop algorithm decision state.
/// </remarks>
/// <seealso cref="PumpSnapshot"/>
/// <seealso cref="IApsSnapshotRepository"/>
/// <seealso cref="IUploaderSnapshotRepository"/>
/// <seealso cref="IV4Repository{T}"/>
public interface IPumpSnapshotRepository : IV4Repository<PumpSnapshot>
{
    /// <summary>Retrieve a page of <see cref="PumpSnapshot"/> records filtered by time range, device, and source.</summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<IEnumerable<PumpSnapshot>> GetAsync(DateTime? from, DateTime? to, string? device, string? source, int limit = 100, int offset = 0, bool descending = true, CancellationToken ct = default);

    /// <summary>Returns a single <see cref="PumpSnapshot"/> by its UUID v7, or <c>null</c> if not found.</summary>
    /// <param name="id">UUID v7 record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<PumpSnapshot?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Retrieve a <see cref="PumpSnapshot"/> by its original MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching record, or <c>null</c> if not found.</returns>
    Task<PumpSnapshot?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Persist a new <see cref="PumpSnapshot"/> and return the saved entity.</summary>
    /// <param name="model">Record to create.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<PumpSnapshot> CreateAsync(PumpSnapshot model, CancellationToken ct = default);

    /// <summary>Replace an existing <see cref="PumpSnapshot"/> identified by <paramref name="id"/>.</summary>
    /// <param name="id">UUID v7 identifier of the record to update.</param>
    /// <param name="model">Updated record data.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<PumpSnapshot> UpdateAsync(Guid id, PumpSnapshot model, CancellationToken ct = default);

    /// <summary>Delete a <see cref="PumpSnapshot"/> by its UUID v7.</summary>
    /// <param name="id">UUID v7 identifier of the record to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Delete the <see cref="PumpSnapshot"/> with the given legacy MongoDB ObjectId.</summary>
    /// <param name="legacyId">Original MongoDB ObjectId string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted (0 or 1).</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>Retrieve <see cref="PumpSnapshot"/> records matching any of the given correlation IDs.</summary>
    /// <param name="correlationIds">Correlation IDs to match.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<PumpSnapshot>> GetByCorrelationIdsAsync(IEnumerable<Guid> correlationIds, CancellationToken ct = default);

    /// <summary>Count <see cref="PumpSnapshot"/> records within an optional time range.</summary>
    /// <param name="from">Inclusive start, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Exclusive end, or <c>null</c> for no upper bound.</param>
    /// <param name="ct">Cancellation token.</param>
    new Task<int> CountAsync(DateTime? from, DateTime? to, CancellationToken ct = default);
}
