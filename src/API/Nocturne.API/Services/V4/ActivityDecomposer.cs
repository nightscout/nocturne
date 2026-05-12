using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Decomposes legacy <see cref="Activity"/> records into typed v4 models (<see cref="HeartRate"/> or
/// <see cref="StepCount"/>). Detection is based on the presence of specific keys in
/// <see cref="Activity.AdditionalProperties"/>: <c>bpm</c> indicates heart-rate data; <c>metric</c>
/// indicates step-count data. Supports idempotent create-or-update via <c>OriginalId</c> matching.
/// </summary>
/// <seealso cref="IActivityDecomposer"/>
/// <seealso cref="IDecomposer{T}"/>
public class ActivityDecomposer : IActivityDecomposer, IDecomposer<Activity>
{
    private readonly NocturneDbContext _dbContext;
    private readonly IStateSpanRepository _stateSpanRepository;
    private readonly ILogger<ActivityDecomposer> _logger;

    /// <param name="dbContext">EF Core context used for direct entity read/write operations.</param>
    /// <param name="stateSpanRepository">Repository for bulk-creating regular activities as StateSpans.</param>
    /// <param name="logger">Logger instance for this decomposer.</param>
    public ActivityDecomposer(
        NocturneDbContext dbContext,
        IStateSpanRepository stateSpanRepository,
        ILogger<ActivityDecomposer> logger)
    {
        _dbContext = dbContext;
        _stateSpanRepository = stateSpanRepository;
        _logger = logger;
    }

    /// <summary>
    /// Returns <see langword="true"/> if the activity carries heart-rate data (identified by the
    /// presence of a <c>bpm</c> key in <see cref="Activity.AdditionalProperties"/>).
    /// </summary>
    /// <param name="activity">The activity to inspect.</param>
    /// <returns><see langword="true"/> when the activity has a <c>bpm</c> property; otherwise <see langword="false"/>.</returns>
    public bool IsHeartRate(Activity activity)
    {
        return activity.AdditionalProperties != null
            && activity.AdditionalProperties.ContainsKey("bpm");
    }

    /// <summary>
    /// Returns <see langword="true"/> if the activity carries step-count data (identified by the
    /// presence of a <c>metric</c> key in <see cref="Activity.AdditionalProperties"/>).
    /// </summary>
    /// <param name="activity">The activity to inspect.</param>
    /// <returns><see langword="true"/> when the activity has a <c>metric</c> property; otherwise <see langword="false"/>.</returns>
    public bool IsStepCount(Activity activity)
    {
        return activity.AdditionalProperties != null
            && activity.AdditionalProperties.ContainsKey("metric");
    }

    /// <summary>
    /// Returns <see langword="true"/> if the activity represents sensor-derived physiological data,
    /// i.e. it is either a heart-rate or step-count record.
    /// </summary>
    /// <param name="activity">The activity to inspect.</param>
    /// <returns><see langword="true"/> when the activity is either heart-rate or step-count data.</returns>
    public bool IsSensorData(Activity activity)
    {
        return IsHeartRate(activity) || IsStepCount(activity);
    }

    /// <inheritdoc/>
    public async Task<DecompositionResult> DecomposeAsync(
        Activity activity,
        CancellationToken ct = default
    )
    {
        var batch = new DecompositionBatchEntity
        {
            TenantId = _dbContext.TenantId,
            Source = "activity_decomposer",
            SourceRecordId = activity.Id,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.DecompositionBatches.Add(batch);
        await _dbContext.SaveChangesAsync(ct);

        var result = new DecompositionResult { CorrelationId = batch.Id };

        if (IsHeartRate(activity))
        {
            await DecomposeHeartRateAsync(activity, result, ct);
        }
        else if (IsStepCount(activity))
        {
            await DecomposeStepCountAsync(activity, result, ct);
        }
        else
        {
            _logger.LogDebug(
                "Activity {Id} is a regular activity, skipping decomposition",
                activity.Id
            );
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<DecompositionResult> DecomposeBatchAsync(
        IReadOnlyList<Activity> activities, CancellationToken ct = default)
    {
        if (activities.Count == 0)
            return new DecompositionResult();

        var batch = new DecompositionBatchEntity
        {
            TenantId = _dbContext.TenantId,
            Source = "activity_decomposer_batch",
            SourceRecordId = null,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.DecompositionBatches.Add(batch);
        await _dbContext.SaveChangesAsync(ct);

        var result = new DecompositionResult { CorrelationId = batch.Id };

        var heartRateList = new List<HeartRate>();
        var stepCountList = new List<StepCount>();
        var regularActivities = new List<Activity>();

        foreach (var activity in activities)
        {
            if (IsHeartRate(activity))
                heartRateList.Add(MapToHeartRate(activity));
            else if (IsStepCount(activity))
                stepCountList.Add(MapToStepCount(activity));
            else
                regularActivities.Add(activity);
        }

        if (heartRateList.Count > 0)
        {
            // Filter out records that already exist by OriginalId to avoid duplicates on re-migration
            var hrOriginalIds = heartRateList
                .Where(hr => hr.Id != null)
                .Select(hr => hr.Id!)
                .ToHashSet();

            var existingHrIds = hrOriginalIds.Count > 0
                ? (await _dbContext.HeartRates
                    .Where(h => h.OriginalId != null && hrOriginalIds.Contains(h.OriginalId))
                    .Select(h => h.OriginalId!)
                    .ToListAsync(ct))
                    .ToHashSet()
                : new HashSet<string>();

            var newHeartRates = heartRateList
                .Where(hr => hr.Id == null || !existingHrIds.Contains(hr.Id))
                .ToList();

            if (newHeartRates.Count > 0)
            {
                var entities = newHeartRates.Select(HeartRateMapper.ToEntity).ToList();
                await _dbContext.HeartRates.AddRangeAsync(entities, ct);
                await _dbContext.SaveChangesAsync(ct);
                result.CreatedRecords.AddRange(entities.Select(HeartRateMapper.ToDomainModel));
            }

            if (existingHrIds.Count > 0)
                _logger.LogDebug("Skipped {Count} duplicate heart rate records by OriginalId", existingHrIds.Count);
        }

        if (stepCountList.Count > 0)
        {
            // Filter out records that already exist by OriginalId to avoid duplicates on re-migration
            var scOriginalIds = stepCountList
                .Where(sc => sc.Id != null)
                .Select(sc => sc.Id!)
                .ToHashSet();

            var existingScIds = scOriginalIds.Count > 0
                ? (await _dbContext.StepCounts
                    .Where(s => s.OriginalId != null && scOriginalIds.Contains(s.OriginalId))
                    .Select(s => s.OriginalId!)
                    .ToListAsync(ct))
                    .ToHashSet()
                : new HashSet<string>();

            var newStepCounts = stepCountList
                .Where(sc => sc.Id == null || !existingScIds.Contains(sc.Id))
                .ToList();

            if (newStepCounts.Count > 0)
            {
                var entities = newStepCounts.Select(StepCountMapper.ToEntity).ToList();
                await _dbContext.StepCounts.AddRangeAsync(entities, ct);
                await _dbContext.SaveChangesAsync(ct);
                result.CreatedRecords.AddRange(entities.Select(StepCountMapper.ToDomainModel));
            }

            if (existingScIds.Count > 0)
                _logger.LogDebug("Skipped {Count} duplicate step count records by OriginalId", existingScIds.Count);
        }

        if (regularActivities.Count > 0)
        {
            var stateSpans = regularActivities.Select(ActivityStateSpanMapper.ToStateSpan).ToList();
            var created = await _stateSpanRepository.CreateActivitiesAsStateSpansAsync(stateSpans, ct);
            result.CreatedRecords.AddRange(created.Select(s => ActivityStateSpanMapper.ToActivity(s)!));
        }

        _logger.LogDebug(
            "Batch-decomposed {Count} activities ({HeartRate} HR, {StepCount} steps, {Regular} regular)",
            activities.Count, heartRateList.Count, stepCountList.Count, regularActivities.Count);

        return result;
    }

    /// <inheritdoc/>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var deleted = 0;

        var heartRateEntity = await _dbContext.HeartRates.FirstOrDefaultAsync(
            h => h.OriginalId == legacyId,
            ct
        );
        if (heartRateEntity != null)
        {
            _dbContext.HeartRates.Remove(heartRateEntity);
            deleted++;
        }

        var stepCountEntity = await _dbContext.StepCounts.FirstOrDefaultAsync(
            s => s.OriginalId == legacyId,
            ct
        );
        if (stepCountEntity != null)
        {
            _dbContext.StepCounts.Remove(stepCountEntity);
            deleted++;
        }

        if (deleted > 0)
        {
            await _dbContext.SaveChangesAsync(ct);
            _logger.LogDebug(
                "Deleted {Count} decomposed records for legacy activity {LegacyId}",
                deleted,
                legacyId
            );
        }

        return deleted;
    }

    // --- Reverse mapping for backward-compat GET ---

    /// <summary>
    /// Reconstructs a legacy <see cref="Activity"/> from a stored <see cref="HeartRate"/> record
    /// for backward-compatible GET responses on the v1/v3 activities endpoint.
    /// </summary>
    /// <param name="heartRate">The v4 heart-rate record to reverse-map.</param>
    /// <returns>An <see cref="Activity"/> with <c>bpm</c> and <c>accuracy</c> in its additional properties.</returns>
    internal static Activity HeartRateToActivity(HeartRate heartRate)
    {
        var activity = new Activity
        {
            Id = heartRate.Id,
            Mills = heartRate.Mills,
            CreatedAt = heartRate.CreatedAt,
            UtcOffset = heartRate.UtcOffset,
            EnteredBy = heartRate.EnteredBy,
            AdditionalProperties = new Dictionary<string, object>
            {
                ["bpm"] = heartRate.Bpm,
                ["accuracy"] = heartRate.Accuracy,
            },
        };

        if (heartRate.Device != null)
            activity.AdditionalProperties["device"] = heartRate.Device;

        return activity;
    }

    /// <summary>
    /// Reconstructs a legacy <see cref="Activity"/> from a stored <see cref="StepCount"/> record
    /// for backward-compatible GET responses on the v1/v3 activities endpoint.
    /// </summary>
    /// <param name="stepCount">The v4 step-count record to reverse-map.</param>
    /// <returns>An <see cref="Activity"/> with <c>metric</c> and <c>source</c> in its additional properties.</returns>
    internal static Activity StepCountToActivity(StepCount stepCount)
    {
        var activity = new Activity
        {
            Id = stepCount.Id,
            Mills = stepCount.Mills,
            CreatedAt = stepCount.CreatedAt,
            UtcOffset = stepCount.UtcOffset,
            EnteredBy = stepCount.EnteredBy,
            AdditionalProperties = new Dictionary<string, object>
            {
                ["metric"] = stepCount.Metric,
                ["source"] = stepCount.Source,
            },
        };

        if (stepCount.Device != null)
            activity.AdditionalProperties["device"] = stepCount.Device;

        return activity;
    }

    // --- Private decomposition methods ---

    private async Task DecomposeHeartRateAsync(
        Activity activity,
        DecompositionResult result,
        CancellationToken ct
    )
    {
        var existing =
            activity.Id != null
                ? await _dbContext.HeartRates.FirstOrDefaultAsync(
                    h => h.OriginalId == activity.Id,
                    ct
                )
                : null;

        var heartRate = MapToHeartRate(activity);

        if (existing != null)
        {
            HeartRateMapper.UpdateEntity(existing, heartRate);
            await _dbContext.SaveChangesAsync(ct);
            result.UpdatedRecords.Add(HeartRateMapper.ToDomainModel(existing));
            _logger.LogDebug(
                "Updated existing HeartRate {Id} from legacy activity {LegacyId}",
                existing.Id,
                activity.Id
            );
        }
        else
        {
            var entity = HeartRateMapper.ToEntity(heartRate);
            await _dbContext.HeartRates.AddAsync(entity, ct);
            await _dbContext.SaveChangesAsync(ct);
            result.CreatedRecords.Add(HeartRateMapper.ToDomainModel(entity));
            _logger.LogDebug("Created HeartRate from legacy activity {LegacyId}", activity.Id);
        }
    }

    private async Task DecomposeStepCountAsync(
        Activity activity,
        DecompositionResult result,
        CancellationToken ct
    )
    {
        var existing =
            activity.Id != null
                ? await _dbContext.StepCounts.FirstOrDefaultAsync(
                    s => s.OriginalId == activity.Id,
                    ct
                )
                : null;

        var stepCount = MapToStepCount(activity);

        if (existing != null)
        {
            StepCountMapper.UpdateEntity(existing, stepCount);
            await _dbContext.SaveChangesAsync(ct);
            result.UpdatedRecords.Add(StepCountMapper.ToDomainModel(existing));
            _logger.LogDebug(
                "Updated existing StepCount {Id} from legacy activity {LegacyId}",
                existing.Id,
                activity.Id
            );
        }
        else
        {
            var entity = StepCountMapper.ToEntity(stepCount);
            await _dbContext.StepCounts.AddAsync(entity, ct);
            await _dbContext.SaveChangesAsync(ct);
            result.CreatedRecords.Add(StepCountMapper.ToDomainModel(entity));
            _logger.LogDebug("Created StepCount from legacy activity {LegacyId}", activity.Id);
        }
    }

    // --- Mapping helpers ---

    internal static HeartRate MapToHeartRate(Activity activity)
    {
        var props = activity.AdditionalProperties ?? new Dictionary<string, object>();

        return new HeartRate
        {
            Id = activity.Id,
            Mills = activity.Mills,
            Bpm = GetIntValue(props, "bpm"),
            Accuracy = GetIntValue(props, "accuracy"),
            Device = GetStringValue(props, "device") ?? activity.EnteredBy,
            EnteredBy = activity.EnteredBy,
            CreatedAt = activity.CreatedAt,
            UtcOffset = activity.UtcOffset,
        };
    }

    internal static StepCount MapToStepCount(Activity activity)
    {
        var props = activity.AdditionalProperties ?? new Dictionary<string, object>();

        return new StepCount
        {
            Id = activity.Id,
            Mills = activity.Mills,
            Metric = GetIntValue(props, "metric"),
            Source = GetIntValue(props, "source"),
            Device = GetStringValue(props, "device") ?? activity.EnteredBy,
            EnteredBy = activity.EnteredBy,
            CreatedAt = activity.CreatedAt,
            UtcOffset = activity.UtcOffset,
        };
    }

    private static int GetIntValue(Dictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out var value))
            return 0;

        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            System.Text.Json.JsonElement je
                when je.ValueKind == System.Text.Json.JsonValueKind.Number
                => je.GetInt32(),
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => 0,
        };
    }

    private static string? GetStringValue(Dictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out var value))
            return null;

        return value switch
        {
            string s => s,
            System.Text.Json.JsonElement je
                when je.ValueKind == System.Text.Json.JsonValueKind.String
                => je.GetString(),
            _ => value?.ToString(),
        };
    }
}
