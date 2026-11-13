using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Repositories;

/// <summary>
/// The stored records of one health series (body weight, heart rate, step count), one context of the
/// scope's reader per call.
/// </summary>
/// <remarks>
/// A route id is the record's own id or the MongoDB ObjectId it carried into the migration, so a
/// link minted against the legacy database keeps resolving.
/// </remarks>
/// <typeparam name="T">The domain record type.</typeparam>
public interface IHealthSeriesRepository<T>
    where T : class
{
    /// <summary>A page of records, newest first.</summary>
    Task<IReadOnlyList<T>> GetNewestFirstAsync(int count, int skip, CancellationToken ct = default);

    /// <summary>The record with the given route id, or <see langword="null"/>.</summary>
    Task<T?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Stores <paramref name="records"/> and returns what was stored. A record whose
    /// (DataSource, SyncIdentifier) or id is held by a live row updates it in place; one held by a
    /// row the user deleted is dropped. A batch that repeats a sync key keeps its last occurrence.
    /// </summary>
    Task<IReadOnlyList<T>> UpsertAsync(IReadOnlyCollection<T> records, CancellationToken ct = default);

    /// <summary>
    /// Overwrites the record with the given route id, or returns <see langword="null"/> when there is none.
    /// </summary>
    Task<T?> UpdateAsync(string id, T record, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes the record with the given route id. Returns <see langword="false"/> when there is none.
    /// </summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IHealthSeriesRepository{T}"/> for a series keyed on the instant each measurement was
/// observed, with the reads that timestamp makes possible.
/// </summary>
/// <typeparam name="T">The domain record type.</typeparam>
public interface IObservedHealthSeriesRepository<T> : IHealthSeriesRepository<T>
    where T : class
{
    /// <summary>A page of records newest first, limited to those observed in <c>[from, to]</c>.</summary>
    /// <param name="count">Maximum number of records to return.</param>
    /// <param name="skip">Number of records to skip.</param>
    /// <param name="from">Inclusive lower bound, or <see langword="null"/> for none.</param>
    /// <param name="to">Inclusive upper bound, or <see langword="null"/> for none.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<T>> GetNewestFirstAsync(
        int count, int skip, DateTime? from, DateTime? to, CancellationToken ct = default);

    /// <summary>
    /// Records observed in <c>[from, to)</c>, oldest first. A <see langword="null"/>
    /// <paramref name="count"/> returns every record in the range.
    /// </summary>
    Task<IReadOnlyList<T>> GetByDateRangeAsync(
        DateTime from, DateTime to, int? count, int skip, CancellationToken ct = default);

    /// <summary>
    /// The latest timestamp written by <paramref name="source"/> (a connector's resume watermark), or
    /// <see langword="null"/> when that source has written none.
    /// </summary>
    Task<DateTime?> GetLatestTimestampAsync(string source, CancellationToken ct = default);
}

/// <summary><see cref="IObservedHealthSeriesRepository{T}"/> for heart rate.</summary>
public interface IHeartRateRepository : IObservedHealthSeriesRepository<HeartRate>
{
    /// <summary>
    /// One reading per UTC minute observed in <c>[from, to)</c>, its rounded mean, oldest first.
    /// Grouped in the database so a dense wearable stream does not cross into the caller.
    /// </summary>
    Task<IReadOnlyList<HeartRate>> GetMinuteAveragesAsync(
        DateTime from, DateTime to, CancellationToken ct = default);
}
