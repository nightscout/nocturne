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
using Nocturne.Infrastructure.Data.Extensions;
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

        await BulkUpsertByOriginalIdAsync(
            _dbContext.HeartRates, heartRateList, HeartRateMapper.ToEntity,
            HeartRateMapper.UpdateEntity, HeartRateMapper.ToDomainModel, result, ct);

        await BulkUpsertByOriginalIdAsync(
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
    /// Soft-deletes, as <c>SimpleEntityService.DeleteOneAsync</c> does: the tombstone a user's delete
    /// leaves is what keeps a connector resync from bringing the record back
    /// (<see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>).
    /// </remarks>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default)
    {
        var deleted = 0;
        var now = DateTime.UtcNow;

        var heartRateEntity = await _dbContext.HeartRates.FirstOrDefaultAsync(
            h => h.OriginalId == legacyId,
            ct
        );
        if (heartRateEntity != null)
        {
            heartRateEntity.DeletedAt = now;
            deleted++;
        }

        var stepCountEntity = await _dbContext.StepCounts.FirstOrDefaultAsync(
            s => s.OriginalId == legacyId,
            ct
        );
        if (stepCountEntity != null)
        {
            stepCountEntity.DeletedAt = now;
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
    /// writes the entity through the context. The <c>OriginalId</c> resolves as
    /// <see cref="ResolveByOriginalIdAsync{TEntity}"/> describes, the sync key as
    /// <see cref="ResolveBySyncKeyAsync{TEntity}"/> does.
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
        where TEntity : class, ITenantScoped, ISoftDeletable, IOriginalIdentified, ISyncDedupable
    {
        var recordType = typeof(TModel).Name;
        var entity = toEntity(model);
        TEntity? existing;
        if (model.Id != null)
        {
            var (rows, userDeleted) = await ResolveByOriginalIdAsync(set, [model.Id], ct);
            if (userDeleted.Count > 0)
            {
                result.SkippedDeleted++;
                _logger.LogDebug("Skipped {RecordType} from legacy activity {LegacyId}: the user deleted it", recordType, model.Id);
                return;
            }

            existing = rows.GetValueOrDefault(model.Id);
        }
        else if (entity.SyncIdentifier != null)
        {
            var key = (entity.DataSource, entity.SyncIdentifier);
            var (rows, userDeleted) = await ResolveBySyncKeyAsync(set, [entity.SyncIdentifier], ct);
            if (userDeleted.Contains(key))
            {
                result.SkippedDeleted++;
                _logger.LogDebug("Skipped {RecordType} with sync key {SyncIdentifier}: the user deleted it", recordType, entity.SyncIdentifier);
                return;
            }

            existing = rows.GetValueOrDefault(key);
        }
        else
        {
            existing = null;
        }

        if (existing != null)
        {
            applyUpdate(existing, model);
            existing.DeletedAt = null;
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

    /// <summary>
    /// The row each of <paramref name="originalIds"/> is written to, soft-deleted rows included: the
    /// live row, or else a system-swept tombstone, which the write revives in place because the primary
    /// key is derived from the id (<see cref="MapperHelpers.ParseIdToGuid"/>) and a fresh insert would
    /// collide with it. An id only a user tombstone holds is returned in <c>UserDeleted</c> and must
    /// not be written (<see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>).
    /// </summary>
    private async Task<(Dictionary<string, TEntity> Rows, HashSet<string> UserDeleted)> ResolveByOriginalIdAsync<TEntity>(
        DbSet<TEntity> set, IReadOnlyCollection<string> originalIds, CancellationToken ct)
        where TEntity : class, ITenantScoped, ISoftDeletable, IOriginalIdentified
    {
        var rows = new Dictionary<string, TEntity>(StringComparer.Ordinal);
        var userDeleted = new HashSet<string>(StringComparer.Ordinal);
        if (originalIds.Count == 0)
            return (rows, userDeleted);

        var ids = originalIds.ToList();
        var candidates = await set.IgnoreQueryFilters()
            .Where(e => e.TenantId == _dbContext.TenantId && e.OriginalId != null && ids.Contains(e.OriginalId))
            .ToListAsync(ct);

        foreach (var group in candidates.GroupBy(e => e.OriginalId!, StringComparer.Ordinal))
        {
            if (group.FirstOrDefault(e => e.DeletedAt == null) is { } live)
                rows[group.Key] = live;
            else if (group.Any(e => _dbContext.Entry(e).Property<bool>("DeletedByUser").CurrentValue))
                userDeleted.Add(group.Key);
            else
                rows[group.Key] = group.First();
        }

        return (rows, userDeleted);
    }

    /// <summary>
    /// The live row holding each <c>(DataSource, SyncIdentifier)</c> key among
    /// <paramref name="syncIdentifiers"/>, and the keys only a user tombstone holds, which must not be
    /// written (<see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>). A key only a
    /// system sweep holds is in neither: the record inserts beside the tombstone, since the primary
    /// key of a record with no id is fresh and the sync-key unique index counts live rows only.
    /// </summary>
    private async Task<(Dictionary<(string? DataSource, string SyncIdentifier), TEntity> Rows,
        HashSet<(string? DataSource, string SyncIdentifier)> UserDeleted)> ResolveBySyncKeyAsync<TEntity>(
        DbSet<TEntity> set, IReadOnlyCollection<string> syncIdentifiers, CancellationToken ct)
        where TEntity : class, ITenantScoped, ISoftDeletable, ISyncDedupable
    {
        var rows = new Dictionary<(string? DataSource, string SyncIdentifier), TEntity>();
        var userDeleted = new HashSet<(string? DataSource, string SyncIdentifier)>();
        if (syncIdentifiers.Count == 0)
            return (rows, userDeleted);

        var ids = syncIdentifiers.ToList();
        var candidates = await set.IgnoreQueryFilters()
            .Where(e => e.TenantId == _dbContext.TenantId && e.SyncIdentifier != null && ids.Contains(e.SyncIdentifier))
            .WhereBlocksRecreation()
            .ToListAsync(ct);

        foreach (var group in candidates.GroupBy(e => (e.DataSource, SyncIdentifier: e.SyncIdentifier!)))
        {
            var governing = group.GoverningRow()!;
            if (governing.DeletedAt == null)
                rows[group.Key] = governing;
            else
                userDeleted.Add(group.Key);
        }

        return (rows, userDeleted);
    }

    /// <summary>
    /// The batch twin of <see cref="UpsertByOriginalIdAsync{TModel,TEntity}"/>: a record whose
    /// <c>OriginalId</c> resolves to a row (<see cref="ResolveByOriginalIdAsync{TEntity}"/>) updates
    /// it, one only a user tombstone holds is skipped, and the rest are inserted. A record with
    /// a sync key instead resolves by that key (<see cref="ResolveBySyncKeyAsync{TEntity}"/>) the
    /// same way. Of several in the batch with one
    /// key, either kind, the last wins.
    /// </summary>
    private async Task BulkUpsertByOriginalIdAsync<TModel, TEntity>(
        DbSet<TEntity> set,
        List<TModel> models,
        Func<TModel, TEntity> toEntity,
        Action<TEntity, TModel> applyUpdate,
        Func<TEntity, object> toDomain,
        DecompositionResult result,
        CancellationToken ct)
        where TModel : ProcessableDocumentBase
        where TEntity : class, ITenantScoped, ISoftDeletable, IOriginalIdentified, ISyncDedupable
    {
        if (models.Count == 0)
            return;

        var originalIds = models.Where(m => m.Id != null).Select(m => m.Id!).ToHashSet();
        var (stored, userDeleted) = await ResolveByOriginalIdAsync(set, originalIds, ct);
        result.SkippedDeleted += models.Count(m => m.Id != null && userDeleted.Contains(m.Id));
        models = models.Where(m => m.Id == null || !userDeleted.Contains(m.Id)).ToList();

        var updated = new List<TEntity>();
        foreach (var model in models
                     .Where(m => m.Id != null && stored.ContainsKey(m.Id))
                     .GroupBy(m => m.Id!)
                     .Select(g => g.Last()))
        {
            var existing = stored[model.Id!];
            applyUpdate(existing, model);
            existing.DeletedAt = null;
            updated.Add(existing);
        }

        var fresh = models
            .Where(m => m.Id == null || !stored.ContainsKey(m.Id))
            .Select(m => (Model: m, Entity: toEntity(m)))
            .ToList();
        var toInsert = fresh.Where(p => p.Entity.SyncIdentifier == null).Select(p => p.Entity).ToList();
        var keyed = fresh
            .Where(p => p.Entity.SyncIdentifier != null)
            .GroupBy(p => (p.Entity.DataSource, p.Entity.SyncIdentifier))
            .Select(g => g.Last())
            .ToList();

        if (keyed.Count > 0)
        {
            var syncIds = keyed.Select(p => p.Entity.SyncIdentifier!).ToHashSet(StringComparer.Ordinal);
            var (storedByKey, userDeletedKeys) = await ResolveBySyncKeyAsync(set, syncIds, ct);

            foreach (var (model, entity) in keyed)
            {
                var key = (entity.DataSource, entity.SyncIdentifier!);
                if (userDeletedKeys.Contains(key))
                {
                    result.SkippedDeleted++;
                }
                else if (storedByKey.TryGetValue(key, out var existing))
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
