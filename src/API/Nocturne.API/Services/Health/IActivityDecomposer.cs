using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Health;

/// <summary>
/// Decomposes legacy Activity records into dedicated v4 models (HeartRate, StepCount), routed
/// per <see cref="Classify"/>. Other activities pass through unchanged to StateSpan storage.
/// </summary>
/// <seealso cref="IHeartRateService"/>
/// <seealso cref="IStepCountService"/>
public interface IActivityDecomposer
{
    /// <summary>
    /// Decomposes a batch of legacy Activities with a fixed number of queries whatever its size:
    /// heart rates into heart_rates, step counts into step_counts, and the rest as StateSpans.
    /// </summary>
    /// <param name="activities">The activities, in arrival order.</param>
    /// <param name="origin">Write classification of the written records.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>What each activity wrote, by batch index.</returns>
    Task<ActivityDecomposition> DecomposeBatchAsync(
        IReadOnlyList<Activity> activities, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Deletes all dedicated records that were decomposed from a legacy Activity with the given ID.
    /// </summary>
    /// <returns>Total number of records deleted across heart_rates and step_counts tables</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Whether <paramref name="id"/> names an activity the user deleted, by the client id it was
    /// stored under or by the id reads served it under, in any table an activity is stored in
    /// (activity state spans, sleep sessions, heart rates, step counts). A record the system swept
    /// does not count.
    /// </summary>
    Task<bool> IsDeletedByUserAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// The storage the activity is written to: heart-rate data (a <c>bpm</c> key in
    /// <see cref="Activity.AdditionalProperties"/>) first, then step-count data (a <c>metric</c> key,
    /// or, as xDrip uploads it, a <c>steps</c> key and <see cref="Activity.Type"/>
    /// <c>"steps-total"</c>), then a sleep type, else a state span. The write routes and the scope
    /// gates all switch on this rather than ordering the checks themselves.
    /// </summary>
    ActivityDestination Classify(Activity activity);

    /// <summary>
    /// Returns the OAuth write scope of the storage <see cref="Classify"/> routes the activity to:
    /// the heart rate, step count or sleep scope, or <c>treatments.readwrite</c> for a state span.
    /// </summary>
    string RequiredWriteScope(Activity activity);

    /// <summary>
    /// Returns the OAuth read scope required to see the activity, based on the storage it came
    /// from (heart rate, step count, sleep, or StateSpans). The merged activity
    /// read endpoints return records from all four storages in one response, so every record
    /// needs a category to be filtered on.
    /// </summary>
    string RequiredReadScope(Activity activity);
}
