using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Repositories;

/// <summary>
/// The heart rates and step counts an <see cref="Activity"/> batch decomposes into, written by the id
/// or sync key each record arrives under, and the user tombstones that keep an activity's id from
/// being written again.
/// </summary>
public interface IActivityRecordRepository
{
    /// <summary>
    /// Writes both tables in one save. A record whose id or sync key only a user tombstone holds is not
    /// written; of several with one primary key or one sync key the last wins; a record whose row is
    /// stored updates it, reviving a system-swept tombstone in place, and is unchanged when that changes
    /// nothing; the rest are inserted. Each record carries the batch index it is reported under.
    /// </summary>
    Task<ActivitySeriesWrites> WriteHealthSeriesAsync(
        IReadOnlyList<(int Index, HeartRate Record)> heartRates,
        IReadOnlyList<(int Index, StepCount Record)> stepCounts,
        CancellationToken ct = default);

    /// <summary>
    /// Whether the user deleted the activity stored under <paramref name="id"/>, by its legacy id or
    /// the primary key it derives, as a state span, sleep session, heart rate or step count.
    /// </summary>
    Task<bool> IsDeletedByUserAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes the heart rate and the step count stored under <paramref name="legacyId"/> or the
    /// primary key it derives, in one save.
    /// </summary>
    /// <returns>The number of rows deleted.</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);
}

/// <summary>What <see cref="IActivityRecordRepository.WriteHealthSeriesAsync"/> wrote to each table.</summary>
public sealed record ActivitySeriesWrites(ActivitySeriesWrite<HeartRate> HeartRateWrites, ActivitySeriesWrite<StepCount> StepCountWrites);

/// <summary>One table's records by batch index, each as stored after the save.</summary>
/// <param name="DeletedByUser">The indexes of the records a user tombstone held, in batch order.</param>
public sealed record ActivitySeriesWrite<T>(
    IReadOnlyList<int> DeletedByUser,
    IReadOnlyList<(int Index, T Record)> Created,
    IReadOnlyList<(int Index, T Record)> Updated,
    IReadOnlyList<(int Index, T Record)> Unchanged);
