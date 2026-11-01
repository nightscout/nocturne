using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Entries;

/// <summary>
/// Entry reads over the V4 glucose tables (sensor and meter readings, calibrations), projected to the
/// legacy <see cref="Entry"/> shape.
/// </summary>
/// <seealso cref="IEntryCache"/>
/// <seealso cref="EntryQuery"/>
internal interface IEntryStore
{
    /// <summary>
    /// Queries entries using the specified <see cref="EntryQuery"/> parameters.
    /// </summary>
    /// <param name="query">The <see cref="EntryQuery"/> filter and pagination parameters.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A read-only list of <see cref="Entry"/> records matching the query.</returns>
    Task<IReadOnlyList<Entry>> QueryAsync(EntryQuery query, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent entry for the current tenant.
    /// </summary>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The current <see cref="Entry"/>, or <c>null</c> if no entries exist.</returns>
    Task<Entry?> GetCurrentAsync(CancellationToken ct = default);

    /// <param name="id">The entry identifier (GUID or legacy MongoDB ObjectId).</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The <see cref="Entry"/> if found, or <c>null</c>.</returns>
    Task<Entry?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Returns the stored record <see cref="GetByIdAsync"/> projects for <paramref name="id"/>: a
    /// <see cref="SensorGlucose"/>, <see cref="MeterGlucose"/> or <see cref="Calibration"/>.
    /// </summary>
    /// <param name="id">The entry identifier, resolved exactly as <see cref="GetByIdAsync"/> resolves it.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The stored record, or <c>null</c>.</returns>
    Task<IV4Record?> GetStoredByIdAsync(string id, CancellationToken ct = default);

    /// <inheritdoc cref="IEntryService.CheckForDuplicateEntryAsync"/>
    Task<Entry?> CheckDuplicateAsync(string? device, string type, long mills, CancellationToken ct = default);

    /// <summary>
    /// Checks a whole upload batch for duplicates, returning one result per probe in the order
    /// given (<c>null</c> where nothing is stored).
    /// </summary>
    /// <remarks>
    /// Classification is identical to <see cref="CheckDuplicateAsync"/> per probe. For <c>sgv</c> —
    /// the only type that arrives in thousands-per-cycle uploads — the stored readings covering the
    /// batch are loaded in one query instead of a query per entry. Other types are probed one at a
    /// time.
    /// </remarks>
    /// <param name="probes">The entries to classify, in submission order.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>Positionally aligned results: the stored reading, or <c>null</c>.</returns>
    Task<IReadOnlyList<StoredEntryMatch?>> CheckDuplicatesAsync(
        IReadOnlyList<EntryDuplicateProbe> probes, CancellationToken ct = default);

    /// <inheritdoc cref="IEntryService.CountEntriesAsync"/>
    Task<long> CountAsync(string? find = null, string? type = null, CancellationToken ct = default);

    /// <inheritdoc cref="IEntryService.GetEntriesModifiedSinceAsync"/>
    Task<ModifiedSincePage<Entry>> GetModifiedSinceAsync(long cursorMills, int limit, CancellationToken ct = default);
}
