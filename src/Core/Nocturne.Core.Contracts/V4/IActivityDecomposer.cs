using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4;

/// <summary>
/// Decomposes legacy Activity records into dedicated v4 models (HeartRate, StepCount).
/// Activities with "bpm" in AdditionalProperties are routed to heart_rates table.
/// Activities with "metric" in AdditionalProperties are routed to step_counts table.
/// Regular activities (exercise, sleep, etc.) pass through unchanged to StateSpan storage.
/// </summary>
/// <seealso cref="IDecompositionPipeline"/>
/// <seealso cref="IHeartRateService"/>
/// <seealso cref="IStepCountService"/>
public interface IActivityDecomposer
{
    /// <summary>
    /// Classifies an activity and routes it to the appropriate dedicated table if it is sensor data.
    /// Returns a result with created/updated records for heart rate or step count data.
    /// Returns an empty result for regular activities (caller should proceed with StateSpan storage).
    /// </summary>
    Task<DecompositionResult> DecomposeAsync(Activity activity, CancellationToken ct = default);

    /// <summary>
    /// Decomposes a batch of legacy Activities into dedicated v4 models, using bulk insert.
    /// Heart rate records are bulk-inserted into the heart_rates table, step counts into step_counts,
    /// and regular activities are bulk-created as StateSpans.
    /// </summary>
    /// <param name="activities">The legacy Activities to decompose</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>
    /// A <see cref="DecompositionResult"/> containing all created V4 records across all activities.
    /// </returns>
    Task<DecompositionResult> DecomposeBatchAsync(
        IReadOnlyList<Activity> activities, CancellationToken ct = default);

    /// <summary>
    /// Deletes all dedicated records that were decomposed from a legacy Activity with the given ID.
    /// </summary>
    /// <returns>Total number of records deleted across heart_rates and step_counts tables</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);

    /// <summary>
    /// Determines whether an activity represents heart rate data (has "bpm" in AdditionalProperties).
    /// </summary>
    bool IsHeartRate(Activity activity);

    /// <summary>
    /// Determines whether an activity represents step count data (has "metric" in AdditionalProperties).
    /// </summary>
    bool IsStepCount(Activity activity);

    /// <summary>
    /// Returns true for heart rate or step count data that should be routed to a dedicated table
    /// rather than stored as a StateSpan.
    /// </summary>
    bool IsSensorData(Activity activity);
}
