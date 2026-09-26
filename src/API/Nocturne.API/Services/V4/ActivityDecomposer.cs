using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Decomposes legacy <see cref="Activity"/> records into typed v4 models (<see cref="HeartRate"/> or
/// <see cref="StepCount"/>). Detection is based on the presence of specific keys in
/// <see cref="Activity.AdditionalProperties"/>: <c>bpm</c> indicates heart-rate data, and
/// <see cref="IsStepCount"/> identifies step-count data. Supports idempotent create-or-update via
/// <c>OriginalId</c> matching.
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

    /// <inheritdoc />
    public bool IsStepCount(Activity activity)
    {
        return activity.AdditionalProperties != null
            && (activity.AdditionalProperties.ContainsKey("metric")
                || (activity.AdditionalProperties.ContainsKey("steps")
                    && string.Equals(activity.Type, "steps-total", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Precedence is mills, <c>timestamp</c>, <c>timeStamp</c>, <c>created_at</c>. xDrip's
    /// <c>timeStamp</c> is read from extension data because binding is case-sensitive, and it ranks
    /// above <c>created_at</c>, which xDrip truncates to whole seconds.
    /// </summary>
    internal static void NormalizeMills(Activity activity)
    {
        if (ApplyClientTimestamp(activity))
            return;

        if (DateTimeOffset.TryParse(activity.CreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var createdAt))
            activity.Mills = createdAt.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// The first three rungs of <see cref="NormalizeMills"/>: mills, <c>timestamp</c>,
    /// <c>timeStamp</c>. Returns whether Mills is set.
    /// </summary>
    internal static bool ApplyClientTimestamp(Activity activity)
    {
        if (activity.Mills > 0)
            return true;

        var mills = activity.Timestamp is > 0 ? activity.Timestamp.Value
            : activity.AdditionalProperties is { } props ? GetLongValue(props, "timeStamp") : 0;
        if (mills <= 0)
            return false;

        activity.Mills = mills;
        activity.UtcOffset ??= 0;
        return true;
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

    /// <summary>
    /// Returns the OAuth write scope required to persist this activity, based on the dedicated
    /// table it routes to: heart-rate data needs <c>heartrate.readwrite</c>, step-count data
    /// <c>stepcount.readwrite</c>, and sleep-typed activities <c>sleep.readwrite</c>. Regular
    /// activities (exercise, illness, travel) route to StateSpans and carry no category scope,
    /// so this returns <see langword="null"/>. Uses the same predicates as the create/update
    /// routing so the scope gate and the storage destination cannot drift apart.
    /// </summary>
    /// <param name="activity">The activity to classify.</param>
    public string? RequiredWriteScope(Activity activity)
    {
        if (IsHeartRate(activity))
            return Scope.HeartRateReadWrite;
        if (IsStepCount(activity))
            return Scope.StepCountReadWrite;
        if (ActivityStateSpanMapper.IsSleepType(activity.Type))
            return Scope.SleepReadWrite;
        return null;
    }

    /// <summary>
    /// Returns the OAuth read scope required to see this activity. Derived from
    /// <see cref="RequiredWriteScope"/> so the read gate and the storage destination cannot drift
    /// apart. A regular activity reads under <c>treatments.read</c>, which is the scope the legacy
    /// activity read plane has always required for StateSpan-backed activities. A dedicated
    /// destination with no read counterpart falls back to <see cref="Scope.FullAccess"/>,
    /// which only an admin grant holds.
    /// </summary>
    /// <param name="activity">The activity to classify.</param>
    public string RequiredReadScope(Activity activity)
    {
        var writeScope = RequiredWriteScope(activity);
        if (writeScope is null)
            return Scope.TreatmentsRead;

        return Scope.ImpliedReadScope(writeScope) ?? Scope.FullAccess;
    }

    /// <inheritdoc/>
    public async Task<DecompositionResult> DecomposeAsync(
        Activity activity,
        WriteOrigin origin, CancellationToken ct = default
    )
    {
        var result = new DecompositionResult { CorrelationId = Guid.CreateVersion7() };
        NormalizeMills(activity);

        if (IsHeartRate(activity))
        {
            await UpsertByOriginalIdAsync(
                _dbContext.HeartRates, MapToHeartRate(activity), HeartRateMapper.ToEntity,
                HeartRateMapper.UpdateEntity, HeartRateMapper.ToDomainModel, result, ct);
        }
        else if (IsStepCount(activity))
        {
            await UpsertByOriginalIdAsync(
                _dbContext.StepCounts, MapToStepCount(activity), StepCountMapper.ToEntity,
                StepCountMapper.UpdateEntity, StepCountMapper.ToDomainModel, result, ct);
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
        IReadOnlyList<Activity> activities, WriteOrigin origin, CancellationToken ct = default)
    {
        if (activities.Count == 0)
            return new DecompositionResult();

        var result = new DecompositionResult { CorrelationId = Guid.CreateVersion7() };

        var heartRateList = new List<HeartRate>();
        var stepCountList = new List<StepCount>();
        var regularActivities = new List<Activity>();

        foreach (var activity in activities)
        {
            NormalizeMills(activity);

            if (IsHeartRate(activity))
                heartRateList.Add(MapToHeartRate(activity));
            else if (IsStepCount(activity))
                stepCountList.Add(MapToStepCount(activity));
            else
                regularActivities.Add(activity);
        }

        await BulkCreateNewByOriginalIdAsync(
            _dbContext.HeartRates, heartRateList, HeartRateMapper.ToEntity,
            HeartRateMapper.UpdateEntity, HeartRateMapper.ToDomainModel, result, ct);

        await BulkCreateNewByOriginalIdAsync(
            _dbContext.StepCounts, stepCountList, StepCountMapper.ToEntity,
            StepCountMapper.UpdateEntity, StepCountMapper.ToDomainModel, result, ct);

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
    /// <remarks>
    /// Deliberately HARD-deletes (unlike the soft-delete in
    /// <c>SimpleEntityService.DeleteOneAsync</c>): this is the v1 activity
    /// re-migration path, where the legacy row is being replaced wholesale, so a
    /// soft-delete tombstone would only block re-creation by the same legacy id.
    /// </remarks>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default)
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

    /// <summary>
    /// Create-or-update keyed on the legacy <c>OriginalId</c>, or, for a record with no id, on its
    /// sync key when <see cref="MapToStepCount"/> gave it one. Heart rates and step counts have no
    /// V4 repository, so unlike its <see cref="DecomposerBase.UpsertByLegacyIdAsync"/> siblings this
    /// writes the entity through the context.
    /// </summary>
    private async Task UpsertByOriginalIdAsync<TModel, TEntity>(
        DbSet<TEntity> set,
        TModel model,
        Func<TModel, TEntity> toEntity,
        Action<TEntity, TModel> applyUpdate,
        Func<TEntity, object> toDomain,
        DecompositionResult result,
        CancellationToken ct)
        where TModel : ProcessableDocumentBase
        where TEntity : class, IOriginalIdentified, ISyncDedupable
    {
        var recordType = typeof(TModel).Name;
        var entity = toEntity(model);
        var existing = model.Id != null
            ? await set.FirstOrDefaultAsync(e => e.OriginalId == model.Id, ct)
            : await FindBySyncKeyAsync(set, entity.DataSource, entity.SyncIdentifier, ct);

        if (existing != null)
        {
            applyUpdate(existing, model);
            await _dbContext.SaveChangesAsync(ct);
            result.UpdatedRecords.Add(toDomain(existing));
            _logger.LogDebug(
                "Updated existing {RecordType} {Id} from legacy activity {LegacyId}",
                recordType, existing.Id, model.Id);
            return;
        }

        await set.AddAsync(entity, ct);
        await _dbContext.SaveChangesAsync(ct);
        result.CreatedRecords.Add(toDomain(entity));
        _logger.LogDebug("Created {RecordType} from legacy activity {LegacyId}", recordType, model.Id);
    }

    private static Task<TEntity?> FindBySyncKeyAsync<TEntity>(
        DbSet<TEntity> set, string? dataSource, string? syncIdentifier, CancellationToken ct)
        where TEntity : class, ISyncDedupable =>
        syncIdentifier is null
            ? Task.FromResult<TEntity?>(null)
            : set.FirstOrDefaultAsync(e => e.DataSource == dataSource && e.SyncIdentifier == syncIdentifier, ct);

    /// <summary>
    /// Inserts the records whose <c>OriginalId</c> is not already stored, skipping the rest so a
    /// re-migration cannot duplicate them. A record with a sync key instead updates the stored row
    /// with that key, and of several in the batch with one key the last wins.
    /// </summary>
    private async Task BulkCreateNewByOriginalIdAsync<TModel, TEntity>(
        DbSet<TEntity> set,
        List<TModel> models,
        Func<TModel, TEntity> toEntity,
        Action<TEntity, TModel> applyUpdate,
        Func<TEntity, object> toDomain,
        DecompositionResult result,
        CancellationToken ct)
        where TModel : ProcessableDocumentBase
        where TEntity : class, IOriginalIdentified, ISyncDedupable
    {
        if (models.Count == 0)
            return;

        var originalIds = models.Where(m => m.Id != null).Select(m => m.Id!).ToHashSet();
        var stored = originalIds.Count > 0
            ? (await set
                .Where(e => e.OriginalId != null && originalIds.Contains(e.OriginalId))
                .Select(e => e.OriginalId!)
                .ToListAsync(ct))
                .ToHashSet()
            : new HashSet<string>();

        var fresh = models
            .Where(m => m.Id == null || !stored.Contains(m.Id))
            .Select(m => (Model: m, Entity: toEntity(m)))
            .ToList();
        var toInsert = fresh.Where(p => p.Entity.SyncIdentifier == null).Select(p => p.Entity).ToList();
        var keyed = fresh
            .Where(p => p.Entity.SyncIdentifier != null)
            .GroupBy(p => (p.Entity.DataSource, p.Entity.SyncIdentifier))
            .Select(g => g.Last())
            .ToList();

        var updated = new List<TEntity>();
        if (keyed.Count > 0)
        {
            var syncIds = keyed.Select(p => p.Entity.SyncIdentifier!).ToHashSet();
            var storedByKey = (await set
                    .Where(e => e.SyncIdentifier != null && syncIds.Contains(e.SyncIdentifier))
                    .ToListAsync(ct))
                .GroupBy(e => (e.DataSource, e.SyncIdentifier))
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var (model, entity) in keyed)
            {
                if (storedByKey.TryGetValue((entity.DataSource, entity.SyncIdentifier), out var existing))
                {
                    applyUpdate(existing, model);
                    updated.Add(existing);
                }
                else
                {
                    toInsert.Add(entity);
                }
            }
        }

        if (toInsert.Count > 0 || updated.Count > 0)
        {
            await set.AddRangeAsync(toInsert, ct);
            await _dbContext.SaveChangesAsync(ct);
            result.CreatedRecords.AddRange(toInsert.Select(toDomain));
            result.UpdatedRecords.AddRange(updated.Select(toDomain));
        }

        if (stored.Count > 0)
            _logger.LogDebug(
                "Skipped {Count} {RecordType} records already stored by OriginalId",
                stored.Count, typeof(TModel).Name);
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
            DataSource = activity.DataSource,
        };
    }

    /// <summary>
    /// Maps a step-count activity. An xDrip <c>steps-total</c> record gets
    /// <see cref="StepCount.PossibleRunningTotalFlag"/>. Without an id it also gets a sync key built
    /// from its time, which xDrip keeps unique per record and resends when the last record's count
    /// grows, so a resend updates the stored row.
    /// </summary>
    internal static StepCount MapToStepCount(Activity activity)
    {
        var props = activity.AdditionalProperties ?? new Dictionary<string, object>();
        var hasMetric = props.ContainsKey("metric");
        var isXDripSteps = !hasMetric
            && string.Equals(activity.Type, "steps-total", StringComparison.OrdinalIgnoreCase);

        var stepCount = new StepCount
        {
            Id = activity.Id,
            Mills = activity.Mills,
            Metric = hasMetric ? GetIntValue(props, "metric") : GetIntValue(props, "steps"),
            // StepCount.Source is the absolute/delta bitmask, not provenance — that is DataSource.
            Source = GetIntValue(props, "source") | (isXDripSteps ? StepCount.PossibleRunningTotalFlag : 0),
            Device = GetStringValue(props, "device") ?? activity.EnteredBy,
            EnteredBy = activity.EnteredBy,
            CreatedAt = activity.CreatedAt,
            UtcOffset = activity.UtcOffset,
            DataSource = activity.DataSource,
        };

        if (isXDripSteps && activity.Id is null && activity.Mills > 0)
        {
            stepCount.DataSource ??= DataSources.XDrip;
            stepCount.SyncIdentifier = $"steps-total:{activity.Mills}";
        }

        return stepCount;
    }

    private static int GetIntValue(Dictionary<string, object> props, string key) =>
        GetLongValue(props, key) is var l and >= int.MinValue and <= int.MaxValue ? (int)l : 0;

    private static long GetLongValue(Dictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out var value))
            return 0;

        return value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } je
                => je.TryGetInt64(out var n) ? n : (long)je.GetDouble(),
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } je
                when long.TryParse(je.GetString(), out var parsed) => parsed,
            string s when long.TryParse(s, out var parsed) => parsed,
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
