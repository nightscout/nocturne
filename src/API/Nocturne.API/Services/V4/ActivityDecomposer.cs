using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Health;
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
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Decomposes legacy <see cref="Activity"/> records into typed v4 models (<see cref="HeartRate"/> or
/// <see cref="StepCount"/>) as <see cref="Classify"/> routes them. Supports idempotent
/// create-or-update via <c>OriginalId</c> matching.
/// </summary>
/// <seealso cref="IActivityDecomposer"/>
public class ActivityDecomposer : IActivityDecomposer
{
    private const string XDripHeartRateType = "hr-bpm";

    private readonly INocturneDbContextFactory _contexts;
    private readonly IStateSpanRepository _stateSpanRepository;
    private readonly TimeProvider _time;
    private readonly ILogger<ActivityDecomposer> _logger;

    /// <param name="contexts">Opens the context heart rates and step counts are read and written through.</param>
    /// <param name="stateSpanRepository">Repository for bulk-creating regular activities as StateSpans.</param>
    /// <param name="timeProvider">Clock that stamps soft deletes.</param>
    /// <param name="logger">Logger instance for this decomposer.</param>
    public ActivityDecomposer(
        INocturneDbContextFactory contexts,
        IStateSpanRepository stateSpanRepository,
        TimeProvider timeProvider,
        ILogger<ActivityDecomposer> logger)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _stateSpanRepository = stateSpanRepository;
        _time = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public ActivityDestination Classify(Activity activity)
    {
        var props = activity.AdditionalProperties;
        if (props != null && props.ContainsKey("bpm"))
            return ActivityDestination.HeartRate;
        if (props != null
            && (props.ContainsKey("metric")
                || (props.ContainsKey("steps")
                    && string.Equals(activity.Type, "steps-total", StringComparison.OrdinalIgnoreCase))))
            return ActivityDestination.StepCount;
        if (ActivityStateSpanMapper.IsSleepType(activity.Type))
            return ActivityDestination.SleepSession;
        return ActivityDestination.StateSpan;
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

    /// <inheritdoc />
    public string RequiredWriteScope(Activity activity) => Classify(activity) switch
    {
        ActivityDestination.HeartRate => Scope.HeartRateReadWrite,
        ActivityDestination.StepCount => Scope.StepCountReadWrite,
        ActivityDestination.SleepSession => Scope.SleepReadWrite,
        _ => Scope.TreatmentsReadWrite,
    };

    /// <summary>
    /// Returns the OAuth read scope required to see this activity. Derived from
    /// <see cref="RequiredWriteScope"/> so the read gate and the storage destination cannot drift
    /// apart. A destination with no read counterpart falls back to <see cref="Scope.FullAccess"/>,
    /// which only an admin grant holds.
    /// </summary>
    /// <param name="activity">The activity to classify.</param>
    public string RequiredReadScope(Activity activity)
    {
        return Scope.ImpliedReadScope(RequiredWriteScope(activity)) ?? Scope.FullAccess;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Four phases, each one round trip per table whatever the batch's size: the activities are
    /// classified and mapped; the rows their ids and sync keys name are read
    /// (<see cref="ResolveAsync{TModel,TEntity}"/>); each record is planned onto its row in memory
    /// (<see cref="Plan{TModel,TEntity}"/>); and both tables are saved together, then the regular
    /// activities written as state spans.
    /// <para>
    /// <paramref name="origin"/> is unused, since none of the writes made here takes one; it stays for
    /// the shape every decomposer's batch shares.
    /// </para>
    /// </remarks>
    public async Task<ActivityDecomposition> DecomposeBatchAsync(
        IReadOnlyList<Activity> activities, WriteOrigin origin, CancellationToken ct = default)
    {
        var result = new ActivityDecomposition();
        if (activities.Count == 0)
            return result;

        var groups = activities.Select(_ => Guid.CreateVersion7()).ToList();
        result.Begin(groups);
        result.CorrelationId = groups[0];

        var heartRates = new List<(int Index, HeartRate Model)>();
        var stepCounts = new List<(int Index, StepCount Model)>();
        var regular = new List<Activity>();
        for (var i = 0; i < activities.Count; i++)
        {
            var activity = activities[i];
            NormalizeMills(activity);
            switch (Classify(activity))
            {
                case ActivityDestination.HeartRate:
                    heartRates.Add((i, MapToHeartRate(activity)));
                    break;
                case ActivityDestination.StepCount:
                    stepCounts.Add((i, MapToStepCount(activity)));
                    break;
                default:
                    regular.Add(activity);
                    break;
            }
        }

        await using var db = await _contexts.CreateAsync(ct);
        var heartRateRows = await ResolveAsync(db, db.HeartRates, heartRates, HeartRateMapper.ToEntity, ct);
        var stepCountRows = await ResolveAsync(db, db.StepCounts, stepCounts, StepCountMapper.ToEntity, ct);

        var heartRatePlan = Plan(db, db.HeartRates, heartRateRows, HeartRateMapper.UpdateEntity, result);
        var stepCountPlan = Plan(db, db.StepCounts, stepCountRows, StepCountMapper.UpdateEntity, result);

        if (heartRatePlan.Writes || stepCountPlan.Writes)
            await db.SaveChangesAsync(ct);
        heartRatePlan.Report(result, groups, HeartRateMapper.ToDomainModel);
        stepCountPlan.Report(result, groups, StepCountMapper.ToDomainModel);

        if (regular.Count > 0)
        {
            var stateSpans = regular.Select(ActivityStateSpanMapper.ToStateSpan).ToList();
            var created = await _stateSpanRepository.CreateActivitiesAsStateSpansAsync(stateSpans, ct);
            result.CreatedRecords.AddRange(created.Select(s => ActivityStateSpanMapper.ToActivity(s)!));
        }

        _logger.LogDebug(
            "Batch-decomposed {Count} activities ({HeartRate} HR, {StepCount} steps, {Regular} regular)",
            activities.Count, heartRates.Count, stepCounts.Count, regular.Count);

        return result;
    }

    private static readonly List<string> ActivityCategoryNames =
        ActivityStateSpanMapper.ActivityCategories.Select(c => c.ToString()).ToList();

    /// <inheritdoc/>
    public async Task<bool> IsDeletedByUserAsync(string id, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        Guid? recordId = Guid.TryParse(id, out var parsed) ? parsed : null;

        return await db.UserTombstones<StateSpanEntity>().AnyAsync(
                s => ActivityCategoryNames.Contains(s.Category) && (s.OriginalId == id || s.Id == recordId), ct)
            || await db.UserTombstones<SleepSessionEntity>().AnyAsync(
                s => s.OriginalId == id || s.Id == recordId, ct)
            || await db.UserTombstones<HeartRateEntity>().AnyAsync(
                h => h.OriginalId == id || h.Id == recordId, ct)
            || await db.UserTombstones<StepCountEntity>().AnyAsync(
                s => s.OriginalId == id || s.Id == recordId, ct);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Soft-deletes, as <c>SimpleEntityService.DeleteOneAsync</c> does: the tombstone a user's delete
    /// leaves is what keeps a connector resync from bringing the record back
    /// (<see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>).
    /// </remarks>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var deleted = 0;
        var now = _time.GetUtcNow().UtcDateTime;

        var heartRateEntity = await db.HeartRates.FirstOrDefaultAsync(
            h => h.OriginalId == legacyId,
            ct
        );
        if (heartRateEntity != null)
        {
            heartRateEntity.DeletedAt = now;
            deleted++;
        }

        var stepCountEntity = await db.StepCounts.FirstOrDefaultAsync(
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
            await db.SaveChangesAsync(ct);
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
            Type = heartRate.Type,
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
            Type = stepCount.Type,
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

    /// <summary>A table's mapped records, by batch index, with the stored rows their ids and sync keys name.</summary>
    private sealed record Resolved<TModel, TEntity>(
        List<(int Index, TModel Model, TEntity Entity)> Records,
        Dictionary<string, TEntity> ById,
        HashSet<string> UserDeletedIds,
        Dictionary<(string? DataSource, string SyncIdentifier), TEntity> ByKey,
        HashSet<(string? DataSource, string SyncIdentifier)> UserDeletedKeys)
        where TEntity : class;

    /// <summary>
    /// Maps a table's records and reads, in one query each, the rows their <c>OriginalId</c>s name
    /// (<see cref="ResolveByOriginalIdAsync{TEntity}"/>) and the rows the sync keys of those with no id
    /// name (<see cref="ResolveBySyncKeyAsync{TEntity}"/>). Only a record with no id is given a sync
    /// key (<see cref="MapToHeartRate"/>, <see cref="MapToStepCount"/>).
    /// </summary>
    private static async Task<Resolved<TModel, TEntity>> ResolveAsync<TModel, TEntity>(
        NocturneDbContext db, DbSet<TEntity> set, List<(int Index, TModel Model)> models,
        Func<TModel, TEntity> toEntity, CancellationToken ct)
        where TModel : ProcessableDocumentBase
        where TEntity : class, ITenantScoped, ISoftDeletable, IOriginalIdentified, ISyncDedupable
    {
        var records = models.Select(m => (m.Index, m.Model, Entity: toEntity(m.Model))).ToList();
        var (byId, userDeletedIds) = await ResolveByOriginalIdAsync(
            db, set, [.. records.Select(r => r.Model.Id).OfType<string>().Distinct(StringComparer.Ordinal)], ct);
        var (byKey, userDeletedKeys) = await ResolveBySyncKeyAsync(
            db, set,
            [.. records.Where(r => r.Model.Id == null).Select(r => r.Entity.SyncIdentifier).OfType<string>().Distinct(StringComparer.Ordinal)],
            ct);
        return new(records, byId, userDeletedIds, byKey, userDeletedKeys);
    }

    /// <summary>
    /// The rows a table's records write. A record whose id or sync key only a user tombstone holds is
    /// not written; of several with one id or one sync key the last wins; a record whose row is stored
    /// updates it, reviving a system-swept tombstone in place, and is unchanged when that changes
    /// nothing; the rest are inserted.
    /// </summary>
    private static TablePlan<TEntity> Plan<TModel, TEntity>(
        NocturneDbContext db, DbSet<TEntity> set, Resolved<TModel, TEntity> resolved,
        Action<TEntity, TModel> applyUpdate, ActivityDecomposition result)
        where TModel : ProcessableDocumentBase
        where TEntity : class, ITenantScoped, ISoftDeletable, IOriginalIdentified, ISyncDedupable
    {
        var plan = new TablePlan<TEntity>();
        var last = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var p = 0; p < resolved.Records.Count; p++)
        {
            if (KeyOf(resolved.Records[p].Model, resolved.Records[p].Entity) is { } key)
                last[key] = p;
        }

        for (var p = 0; p < resolved.Records.Count; p++)
        {
            var (index, model, entity) = resolved.Records[p];
            TEntity? stored = null;
            if (model.Id is { } id)
            {
                if (resolved.UserDeletedIds.Contains(id))
                {
                    result.DeletedByUser(index);
                    continue;
                }
                resolved.ById.TryGetValue(id, out stored);
            }
            else if (entity.SyncIdentifier is { } syncIdentifier)
            {
                if (resolved.UserDeletedKeys.Contains((entity.DataSource, syncIdentifier)))
                {
                    result.DeletedByUser(index);
                    continue;
                }
                resolved.ByKey.TryGetValue((entity.DataSource, syncIdentifier), out stored);
            }

            if (KeyOf(model, entity) is { } recordKey && last[recordKey] != p)
                continue;

            if (stored is null)
            {
                plan.Inserted.Add((index, entity));
                continue;
            }

            applyUpdate(stored, model);
            stored.DeletedAt = null;
            (db.Entry(stored).State == EntityState.Unchanged ? plan.Unchanged : plan.Updated).Add((index, stored));
        }

        set.AddRange(plan.Inserted.Select(i => i.Entity));
        return plan;
    }

    private static string? KeyOf<TModel, TEntity>(TModel model, TEntity entity)
        where TModel : ProcessableDocumentBase
        where TEntity : ISyncDedupable
        => model.Id is { } id ? $"id|{id}"
            : entity.SyncIdentifier is { } syncIdentifier ? $"sync|{entity.DataSource}|{syncIdentifier}"
            : null;

    /// <summary>What <see cref="Plan{TModel,TEntity}"/> leaves a table's save to write, by batch index.</summary>
    private sealed class TablePlan<TEntity>
    {
        public List<(int Index, TEntity Entity)> Inserted { get; } = [];
        public List<(int Index, TEntity Entity)> Updated { get; } = [];
        public List<(int Index, TEntity Entity)> Unchanged { get; } = [];

        public bool Writes => Inserted.Count > 0 || Updated.Count > 0;

        /// <summary>Folds the committed rows into <paramref name="result"/>, each owned by the activity that wrote it.</summary>
        public void Report(ActivityDecomposition result, IReadOnlyList<Guid> groups, Func<TEntity, object> toDomain)
        {
            Fold(Inserted, result.CreatedRecords);
            Fold(Updated, result.UpdatedRecords);
            Fold(Unchanged, result.UnchangedRecords);

            void Fold(List<(int Index, TEntity Entity)> rows, List<object> into)
            {
                foreach (var (index, entity) in rows)
                {
                    var record = toDomain(entity);
                    result.RecordOwners[record] = groups[index];
                    into.Add(record);
                }
            }
        }
    }

    /// <summary>
    /// The row each of <paramref name="originalIds"/> is written to, soft-deleted rows included: the
    /// live row, or else a system-swept tombstone, which the write revives in place because the primary
    /// key is derived from the id (<see cref="MapperHelpers.ParseIdToGuid"/>) and a fresh insert would
    /// collide with it within the tenant. An id only a user tombstone holds is returned in <c>UserDeleted</c> and must
    /// not be written (<see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>).
    /// </summary>
    private static async Task<(Dictionary<string, TEntity> Rows, HashSet<string> UserDeleted)> ResolveByOriginalIdAsync<TEntity>(
        NocturneDbContext db,
        DbSet<TEntity> set, IReadOnlyCollection<string> originalIds, CancellationToken ct)
        where TEntity : class, ITenantScoped, ISoftDeletable, IOriginalIdentified
    {
        var rows = new Dictionary<string, TEntity>(StringComparer.Ordinal);
        var userDeleted = new HashSet<string>(StringComparer.Ordinal);
        if (originalIds.Count == 0)
            return (rows, userDeleted);

        var ids = originalIds.ToList();
        var candidates = await set.IgnoreQueryFilters()
            .Where(e => e.TenantId == db.TenantId && e.OriginalId != null && ids.Contains(e.OriginalId))
            .ToListAsync(ct);

        foreach (var group in candidates.GroupBy(e => e.OriginalId!, StringComparer.Ordinal))
        {
            if (group.FirstOrDefault(e => e.DeletedAt == null) is { } live)
                rows[group.Key] = live;
            else if (group.Any(e => db.Entry(e).Property<bool>("DeletedByUser").CurrentValue))
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
    private static async Task<(Dictionary<(string? DataSource, string SyncIdentifier), TEntity> Rows,
        HashSet<(string? DataSource, string SyncIdentifier)> UserDeleted)> ResolveBySyncKeyAsync<TEntity>(
        NocturneDbContext db, DbSet<TEntity> set, IReadOnlyCollection<string> syncIdentifiers, CancellationToken ct)
        where TEntity : class, ITenantScoped, ISoftDeletable, ISyncDedupable
    {
        var rows = new Dictionary<(string? DataSource, string SyncIdentifier), TEntity>();
        var userDeleted = new HashSet<(string? DataSource, string SyncIdentifier)>();
        if (syncIdentifiers.Count == 0)
            return (rows, userDeleted);

        var ids = syncIdentifiers.ToList();
        var candidates = await set.IgnoreQueryFilters()
            .Where(e => e.TenantId == db.TenantId && e.SyncIdentifier != null && ids.Contains(e.SyncIdentifier))
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

    // --- Mapping helpers ---

    /// <summary>
    /// Maps a heart-rate activity. An xDrip <c>hr-bpm</c> record without an id gets a sync key built
    /// from its time: xDrip keeps one reading per timestamp and resends the newest reading of each
    /// sync cycle, so a resend updates the stored row.
    /// </summary>
    internal static HeartRate MapToHeartRate(Activity activity)
    {
        var props = activity.AdditionalProperties ?? new Dictionary<string, object>();

        var heartRate = new HeartRate
        {
            Id = activity.Id,
            Type = activity.Type,
            Mills = activity.Mills,
            Bpm = GetIntValue(props, "bpm"),
            Accuracy = GetIntValue(props, "accuracy"),
            Device = GetStringValue(props, "device") ?? activity.EnteredBy,
            EnteredBy = activity.EnteredBy,
            CreatedAt = activity.CreatedAt,
            UtcOffset = activity.UtcOffset,
            DataSource = activity.DataSource,
        };

        if (activity.Id is null && activity.Mills > 0
            && string.Equals(activity.Type, XDripHeartRateType, StringComparison.OrdinalIgnoreCase))
        {
            heartRate.DataSource ??= DataSources.XDrip;
            heartRate.SyncIdentifier = $"{XDripHeartRateType}:{activity.Mills}";
        }

        return heartRate;
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
            Type = activity.Type,
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
