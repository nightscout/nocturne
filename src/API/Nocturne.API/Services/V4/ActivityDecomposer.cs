using Nocturne.API.Services.Health;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Decomposes legacy <see cref="Activity"/> records into typed v4 models (<see cref="HeartRate"/>,
/// <see cref="StepCount"/> or <see cref="SleepSession"/>) as <see cref="Classify"/> routes them. Supports idempotent
/// create-or-update by the id a record arrives under.
/// </summary>
/// <seealso cref="IActivityDecomposer"/>
public class ActivityDecomposer : IActivityDecomposer
{
    private const string XDripHeartRateType = "hr-bpm";

    private readonly IActivityRecordRepository _records;
    private readonly IStateSpanRepository _stateSpanRepository;
    private readonly ISleepService _sleepService;
    private readonly ILogger<ActivityDecomposer> _logger;

    /// <param name="records">Writes heart rates and step counts and reads the activity tombstones.</param>
    /// <param name="stateSpanRepository">Repository for bulk-creating regular activities as StateSpans.</param>
    /// <param name="sleepService">Upserts each sleep activity as a sleep session.</param>
    /// <param name="logger">Logger instance for this decomposer.</param>
    public ActivityDecomposer(
        IActivityRecordRepository records,
        IStateSpanRepository stateSpanRepository,
        ISleepService sleepService,
        ILogger<ActivityDecomposer> logger)
    {
        _records = records ?? throw new ArgumentNullException(nameof(records));
        _stateSpanRepository = stateSpanRepository;
        _sleepService = sleepService;
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
    /// A fixed number of round trips per table whatever the batch's size: the activities are
    /// classified and mapped, heart rates and step counts are written together
    /// (<see cref="IActivityRecordRepository.WriteHealthSeriesAsync"/>), then the regular activities
    /// are written as state spans. Sleep activities are written one by one, each upserted as a
    /// sleep session, and are always reported as created.
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
        var sleep = new List<(int Index, Activity Activity)>();
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
                case ActivityDestination.SleepSession:
                    sleep.Add((i, activity));
                    break;
                default:
                    regular.Add(activity);
                    break;
            }
        }

        var written = await _records.WriteHealthSeriesAsync(heartRates, stepCounts, ct);
        foreach (var index in written.HeartRateWrites.DeletedByUser.Concat(written.StepCountWrites.DeletedByUser))
            result.DeletedByUser(index);
        Report(written.HeartRateWrites, result, groups);
        Report(written.StepCountWrites, result, groups);

        await WriteSleepAsync(sleep, groups, result, ct);

        if (regular.Count > 0)
        {
            var stateSpans = regular.Select(ActivityStateSpanMapper.ToStateSpan).ToList();
            var created = await _stateSpanRepository.CreateActivitiesAsStateSpansAsync(stateSpans, ct);
            result.CreatedRecords.AddRange(created.Select(s => ActivityStateSpanMapper.ToActivity(s)!));
        }

        _logger.LogDebug(
            "Batch-decomposed {Count} activities ({HeartRate} HR, {StepCount} steps, {Sleep} sleep, {Regular} regular)",
            activities.Count, heartRates.Count, stepCounts.Count, sleep.Count, regular.Count);

        return result;
    }

    /// <summary>
    /// Upserts each sleep activity as a <see cref="SleepSession"/>. A session the user deleted is
    /// reported through <see cref="ActivityDecomposition.DeletedByUser"/> and does not fail the batch.
    /// </summary>
    private async Task WriteSleepAsync(
        List<(int Index, Activity Activity)> sleep, IReadOnlyList<Guid> groups, ActivityDecomposition result,
        CancellationToken ct)
    {
        foreach (var (index, activity) in sleep)
        {
            try
            {
                var session = await _sleepService.UpsertSessionAsync(ActivityStateSpanMapper.ToSleepSession(activity), ct);
                var created = ActivityStateSpanMapper.SleepSessionToActivity(session);
                result.RecordOwners[created] = groups[index];
                result.CreatedRecords.Add(created);
            }
            catch (RecreationBlockedException)
            {
                result.DeletedByUser(index);
            }
        }
    }

    /// <summary>Folds a table's committed records into <paramref name="result"/>, each owned by the activity that wrote it.</summary>
    private static void Report<T>(ActivitySeriesWrite<T> written, ActivityDecomposition result, IReadOnlyList<Guid> groups)
        where T : class
    {
        Fold(written.Created, result.CreatedRecords);
        Fold(written.Updated, result.UpdatedRecords);
        Fold(written.Unchanged, result.UnchangedRecords);

        void Fold(IReadOnlyList<(int Index, T Record)> rows, List<object> into)
        {
            foreach (var (index, record) in rows)
            {
                result.RecordOwners[record] = groups[index];
                into.Add(record);
            }
        }
    }

    /// <inheritdoc/>
    public Task<bool> IsDeletedByUserAsync(string id, CancellationToken ct = default) =>
        _records.IsDeletedByUserAsync(id, ct);

    /// <inheritdoc/>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default)
    {
        var deleted = await _records.DeleteByLegacyIdAsync(legacyId, ct);
        if (deleted > 0)
            _logger.LogDebug(
                "Deleted {Count} decomposed records for legacy activity {LegacyId}",
                deleted,
                legacyId
            );

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
