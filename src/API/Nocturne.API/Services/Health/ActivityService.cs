using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.API.Helpers;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
using Nocturne.API.Services.Realtime;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services.Health;

/// <summary>
/// Domain service implementation for <see cref="Activity"/> operations with WebSocket broadcasting.
/// Regular activities are stored as <see cref="StateSpan"/> records via <see cref="IStateSpanService"/>.
/// Sleep-typed activities are stored as <see cref="SleepSession"/> records via <see cref="ISleepService"/>.
/// Heart rate and step count sensor data is routed to dedicated tables via <see cref="IActivityDecomposer"/>.
/// On create, all sources are merged, sorted by <see cref="Activity.Mills"/> descending, and re-paginated.
/// </summary>
/// <seealso cref="IActivityService"/>
/// <seealso cref="IStateSpanService"/>
/// <seealso cref="IActivityDecomposer"/>
/// <seealso cref="IHeartRateService"/>
/// <seealso cref="IStepCountService"/>
/// <seealso cref="ISignalRBroadcastService"/>
public class ActivityService : IActivityService
{
    private readonly IStateSpanService _stateSpanService;
    private readonly ISleepService _sleepService;
    private readonly IDocumentProcessingService _documentProcessingService;
    private readonly ISignalRBroadcastService _signalRBroadcastService;
    private readonly IDataEventSink<Activity> _events;
    private readonly IActivityDecomposer _activityDecomposer;
    private readonly IHeartRateService _heartRateService;
    private readonly IStepCountService _stepCountService;
    private readonly ILogger<ActivityService> _logger;

    /// <summary>
    /// Upper bound on rows pulled from each source when reads merge the four sources in memory and
    /// re-paginate, which defeats limit pushdown. Independent of any controller-level ceiling on
    /// what a caller may request.
    /// </summary>
    private const int MaxOverFetch = 100_000;

    /// <summary>
    /// Rows read from each source when a field filter is evaluated in memory after the fetch.
    /// </summary>
    private const int MaxFilteredFetch = 10_000;

    /// <summary>
    /// Every source <see cref="CountActivitiesByCategoryAsync"/> knows how to count, named by the
    /// read scope its records carry.
    /// </summary>
    private static readonly IReadOnlySet<string> CountableCategories = new HashSet<string>(
        StringComparer.Ordinal)
    {
        Scope.TreatmentsRead,
        Scope.HeartRateRead,
        Scope.StepCountRead,
        Scope.SleepRead,
    };

    /// <summary>
    /// Initializes a new instance of <see cref="ActivityService"/>.
    /// </summary>
    public ActivityService(
        IStateSpanService stateSpanService,
        ISleepService sleepService,
        IDocumentProcessingService documentProcessingService,
        ISignalRBroadcastService signalRBroadcastService,
        IDataEventSink<Activity> events,
        IActivityDecomposer activityDecomposer,
        IHeartRateService heartRateService,
        IStepCountService stepCountService,
        ILogger<ActivityService> logger
    )
    {
        _stateSpanService =
            stateSpanService ?? throw new ArgumentNullException(nameof(stateSpanService));
        _sleepService =
            sleepService ?? throw new ArgumentNullException(nameof(sleepService));
        _documentProcessingService =
            documentProcessingService
            ?? throw new ArgumentNullException(nameof(documentProcessingService));
        _signalRBroadcastService =
            signalRBroadcastService
            ?? throw new ArgumentNullException(nameof(signalRBroadcastService));
        _events =
            events ?? throw new ArgumentNullException(nameof(events));
        _activityDecomposer =
            activityDecomposer ?? throw new ArgumentNullException(nameof(activityDecomposer));
        _heartRateService =
            heartRateService ?? throw new ArgumentNullException(nameof(heartRateService));
        _stepCountService =
            stepCountService ?? throw new ArgumentNullException(nameof(stepCountService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Activity>> GetActivitiesAsync(
        string? find = null,
        int? count = null,
        int? skip = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var actualCount = count ?? 10;
            var actualSkip = skip ?? 0;

            _logger.LogDebug(
                "Getting activity records with find: {Find}, count: {Count}, skip: {Skip}",
                find,
                actualCount,
                actualSkip
            );

            var query = FindQuery.Parse(find);
            var from = query.FromMills is { } fromMills
                ? DateTimeOffset.FromUnixTimeMilliseconds(fromMills).UtcDateTime
                : (DateTime?)null;
            var to = query.ToMills is { } toMills
                ? DateTimeOffset.FromUnixTimeMilliseconds(toMills).UtcDateTime
                : (DateTime?)null;

            // Over-fetch from each source so we can merge and re-paginate. Clamped into range: a
            // large skip overflows the sum, and a non-positive fetch count faults every source
            // query. Callers with no ceiling of their own rely on the upper bound here. A residual
            // field filter runs after the fetch, so the page cannot be pushed down either.
            var fetchCount = query.HasFieldFilters
                ? MaxFilteredFetch
                : (int)Math.Clamp((long)actualCount + actualSkip, 1, MaxOverFetch);

            var stateSpanActivities = await _stateSpanService.GetActivitiesAsync(
                count: fetchCount,
                skip: 0,
                from: from,
                to: to,
                cancellationToken: cancellationToken
            );

            var heartRates = await _heartRateService.GetHeartRatesAsync(
                count: fetchCount,
                skip: 0,
                from: from,
                to: to,
                cancellationToken: cancellationToken
            );
            var heartRateActivities = heartRates.Select(ActivityDecomposer.HeartRateToActivity);

            var stepCounts = await _stepCountService.GetStepCountsAsync(
                count: fetchCount,
                skip: 0,
                from: from,
                to: to,
                cancellationToken: cancellationToken
            );
            var stepCountActivities = stepCounts.Select(ActivityDecomposer.StepCountToActivity);

            var sleepSessions = await _sleepService.GetSessionsAsync(
                from: from,
                to: to,
                limit: fetchCount,
                offset: 0,
                descending: true,
                cancellationToken: cancellationToken
            );
            // The sleep read bounds by overlap; every other source bounds by start, as the find does.
            var sleepActivities = sleepSessions
                .Select(ActivityStateSpanMapper.SleepSessionToActivity)
                .Where(a => StartsWithin(a, query));

            var sources = stateSpanActivities
                .Concat(heartRateActivities)
                .Concat(stepCountActivities)
                .Concat(sleepActivities);

            var merged = (query.HasFieldFilters ? sources.Where(a => query.MatchesResidual(a)) : sources)
                .OrderByDescending(a => a.Mills)
                .Skip(actualSkip)
                .Take(actualCount)
                .ToList();

            return merged;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting activity records");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLatestTimestampAsync(
        string source,
        CancellationToken cancellationToken = default
    )
    {
        // Max over DateTime? skips the destinations this source has never written to, and is null
        // when that is all of them.
        var candidates = new[]
        {
            await _stateSpanService.GetLatestActivityTimestampAsync(source, cancellationToken),
            await _heartRateService.GetLatestTimestampAsync(source, cancellationToken),
            await _stepCountService.GetLatestTimestampAsync(source, cancellationToken),
        };

        return candidates.Max();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Activity>> GetStoredActivitiesAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        return (await ResolveStoredAsync(id, cancellationToken)).Select(s => s.Record).ToList();
    }

    /// <summary>
    /// The records <see cref="GetStoredActivitiesAsync"/> returns, with the storage each came from.
    /// Each storage is looked up the way its own update and delete find a row: a state span by
    /// client id then primary key, a sleep session by primary key or by the (Source, OriginalId)
    /// key <see cref="ActivityStateSpanMapper.ToSleepSession"/> writes, and a heart rate or step
    /// count by primary key for a Guid and by client id otherwise.
    /// </summary>
    private async Task<List<(ActivityDestination Storage, Activity Record)>> ResolveStoredAsync(
        string id,
        CancellationToken cancellationToken
    )
    {
        try
        {
            _logger.LogDebug("Resolving stored activity records for ID: {Id}", id);
            var stored = new List<(ActivityDestination Storage, Activity Record)>();

            if (await _stateSpanService.GetActivityByIdAsync(id, cancellationToken) is { } stateSpan)
                stored.Add((ActivityDestination.StateSpan, stateSpan));

            var byKey = Guid.TryParse(id, out var sessionId)
                ? await _sleepService.GetSessionByIdAsync(sessionId, cancellationToken)
                : null;
            var byRecord = await _sleepService.GetSessionBySourceRecordAsync(
                ActivityStateSpanMapper.SleepSessionSource, id, cancellationToken);
            foreach (var session in new[] { byKey, byRecord }.OfType<SleepSession>().DistinctBy(s => s.Id))
                stored.Add((ActivityDestination.SleepSession, ActivityStateSpanMapper.SleepSessionToActivity(session)));

            if (await _heartRateService.GetHeartRateByIdAsync(id, cancellationToken) is { } heartRate)
                stored.Add((ActivityDestination.HeartRate, ActivityDecomposer.HeartRateToActivity(heartRate)));

            if (await _stepCountService.GetStepCountByIdAsync(id, cancellationToken) is { } stepCount)
                stored.Add((ActivityDestination.StepCount, ActivityDecomposer.StepCountToActivity(stepCount)));

            return stored;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting activity record by ID: {Id}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Activity>> CreateActivitiesAsync(
        IEnumerable<Activity> activities,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var processedList = PrepareForStorage(activities);
            _logger.LogDebug("Creating {Count} activity records", processedList.Count);

            var regularActivities = new List<Activity>();
            var sensorDataActivities = new List<Activity>();
            var sleepActivities = new List<Activity>();

            foreach (var activity in processedList)
            {
                switch (_activityDecomposer.Classify(activity))
                {
                    case ActivityDestination.HeartRate or ActivityDestination.StepCount:
                        sensorDataActivities.Add(activity);
                        break;
                    case ActivityDestination.SleepSession:
                        sleepActivities.Add(activity);
                        break;
                    default:
                        regularActivities.Add(activity);
                        break;
                }
            }

            var results = new List<Activity>();

            // Only a user tombstone is skipped here. Any other failure of the sensor and sleep writes
            // reaches the outer handler, which logs and rethrows it, so the uploader gets an error and
            // does not advance its sync marker past a record never stored. A re-send that changed
            // nothing is answered as stored but not announced.
            var announced = new List<Activity>();
            if (sensorDataActivities.Count > 0)
            {
                var decomposed = await _activityDecomposer.DecomposeBatchAsync(sensorDataActivities, WriteOrigin.Live, cancellationToken);
                var wrote = decomposed.Wrote;
                results.AddRange(sensorDataActivities.Where((_, index) => !decomposed.IsDeletedByUser(index)));
                announced.AddRange(sensorDataActivities.Where((_, index) => wrote[index]));
            }

            foreach (var sleepActivity in sleepActivities)
            {
                try
                {
                    var session = ActivityStateSpanMapper.ToSleepSession(sleepActivity);
                    var created = ActivityStateSpanMapper.SleepSessionToActivity(
                        await _sleepService.UpsertSessionAsync(session, cancellationToken));
                    results.Add(created);
                    announced.Add(created);
                }
                catch (RecreationBlockedException)
                {
                    _logger.LogDebug("Skipped sleep activity {Id}: the user deleted it", sleepActivity.Id);
                }
            }

            // Process regular activities through existing StateSpan path
            if (regularActivities.Count > 0)
            {
                var createdActivities = await _stateSpanService.CreateActivitiesAsync(
                    regularActivities,
                    cancellationToken
                );
                results.AddRange(createdActivities);
                announced.AddRange(createdActivities);
            }

            if (announced.Count > 0)
            {
                await _signalRBroadcastService.BroadcastStorageCreateAsync(
                    "activity",
                    new { collection = "activity", data = announced, count = announced.Count }
                );

                await _events.OnCreatedAsync(announced, cancellationToken);
            }

            _logger.LogDebug(
                "Successfully created {Count} activity records ({SensorCount} sensor, {RegularCount} regular)",
                results.Count,
                sensorDataActivities.Count,
                regularActivities.Count
            );
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating activity records");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Activity?> UpdateActivityAsync(
        string id,
        Activity activity,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Updating activity record with ID: {Id}", id);

            activity = PrepareForStorage([activity]).Single();

            // Try sleep sessions first: GET projects sleep activities with the session Guid as id
            if (Guid.TryParse(id, out var sleepGuid))
            {
                var existingSession = await _sleepService.GetSessionByIdAsync(sleepGuid, cancellationToken);
                if (existingSession != null)
                {
                    var session = ActivityStateSpanMapper.ToSleepSession(activity);
                    // Keep the stored row's dedup key (Source + OriginalId); the v1 payload
                    // carries the session Guid, not the original source record id
                    session.Source = existingSession.Source;
                    session.OriginalId = existingSession.OriginalId;

                    var updatedSession = await _sleepService.UpdateSessionAsync(
                        sleepGuid,
                        session,
                        cancellationToken
                    );
                    if (updatedSession == null)
                        return null;

                    var updatedFromSession = ActivityStateSpanMapper.SleepSessionToActivity(updatedSession);
                    await BroadcastActivityUpdateAsync(updatedFromSession, id, cancellationToken);
                    _logger.LogDebug("Successfully updated sleep session for activity ID: {Id}", id);
                    return updatedFromSession;
                }
            }

            // Sleep-typed payloads whose id is not a session Guid are upserted by
            // OriginalId, matching the row created by CreateActivitiesAsync. Falling
            // through to the StateSpan path would recategorize the record as Exercise.
            if (_activityDecomposer.Classify(activity) == ActivityDestination.SleepSession)
            {
                var sleepSession = ActivityStateSpanMapper.ToSleepSession(activity);
                sleepSession.OriginalId = id;
                // A session Guid that missed the live lookup above may name a tombstone, which the
                // upsert finds only by primary key.
                if (Guid.TryParse(id, out _))
                    sleepSession.Id = id;

                var upsertedSession = await _sleepService.UpsertSessionAsync(sleepSession, cancellationToken);
                var upsertedActivity = ActivityStateSpanMapper.SleepSessionToActivity(upsertedSession);
                await BroadcastActivityUpdateAsync(upsertedActivity, id, cancellationToken);
                _logger.LogDebug("Successfully upserted sleep session for activity ID: {Id}", id);
                return upsertedActivity;
            }

            var updatedActivity = await _stateSpanService.UpdateActivityAsync(
                id,
                activity,
                cancellationToken
            );

            if (updatedActivity != null)
            {
                await BroadcastActivityUpdateAsync(updatedActivity, id, cancellationToken);

                _logger.LogDebug("Successfully updated activity record with ID: {Id}", id);
            }

            return updatedActivity;
        }
        catch (RecreationBlockedException)
        {
            _logger.LogDebug("Refused update of activity {Id}: the user deleted it", id);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating activity record with ID: {Id}", id);
            throw;
        }
    }

    /// <summary>
    /// Sanitises and dates activities the same way for create and update. The client timestamp is
    /// applied first because <see cref="IDocumentProcessingService.ProcessTimestamp"/> would date a
    /// record by whole-second <c>created_at</c>, or by the time of receipt when it is absent.
    /// </summary>
    private List<Activity> PrepareForStorage(IEnumerable<Activity> activities)
    {
        var activityList = activities.ToList();
        foreach (var activity in activityList)
            ActivityDecomposer.ApplyClientTimestamp(activity);

        return _documentProcessingService.ProcessDocuments(activityList).ToList();
    }

    /// <summary>
    /// Broadcasts a storage update over SignalR and raises the updated data event.
    /// </summary>
    private async Task BroadcastActivityUpdateAsync(
        Activity updatedActivity,
        string id,
        CancellationToken cancellationToken
    )
    {
        await _signalRBroadcastService.BroadcastStorageUpdateAsync(
            "activity",
            new { collection = "activity", data = updatedActivity, id = id }
        );

        await _events.OnUpdatedAsync(updatedActivity, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteActivityAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Deleting activity record with ID: {Id}", id);

            var deletedIds = new List<string>();
            try
            {
                foreach (var (storage, record) in await ResolveStoredAsync(id, cancellationToken))
                {
                    switch (storage)
                    {
                        case ActivityDestination.StateSpan:
                            deletedIds.AddRange(await _stateSpanService.DeleteActivityAsync(id, cancellationToken));
                            break;
                        case ActivityDestination.SleepSession:
                            if (await _sleepService.DeleteSessionAsync(Guid.Parse(record.Id!), cancellationToken))
                                deletedIds.Add(record.Id!);
                            break;
                        case ActivityDestination.HeartRate:
                            if (await _heartRateService.DeleteHeartRateAsync(id, cancellationToken))
                                deletedIds.Add(id);
                            break;
                        case ActivityDestination.StepCount:
                            if (await _stepCountService.DeleteStepCountAsync(id, cancellationToken))
                                deletedIds.Add(id);
                            break;
                    }
                }
            }
            catch (Exception) when (deletedIds.Count > 0)
            {
                // Each storage commits on its own, so a later storage failing leaves the earlier
                // deletes committed. They are announced anyway, and a broadcast failure here must not
                // replace the storage failure that is rethrown.
                try
                {
                    await BroadcastDeletedAsync(deletedIds);
                }
                catch (Exception broadcastFailure) when (broadcastFailure is not OperationCanceledException)
                {
                    _logger.LogWarning(broadcastFailure, "Could not announce the activity deletes for ID: {Id}", id);
                }

                throw;
            }

            if (deletedIds.Count > 0)
                await BroadcastDeletedAsync(deletedIds);

            _logger.LogDebug("Deleted {Count} activity records for ID: {Id}", deletedIds.Count, id);
            return deletedIds.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting activity record with ID: {Id}", id);
            throw;
        }
    }

    private async Task BroadcastDeletedAsync(IEnumerable<string> deletedIds)
    {
        foreach (var deletedId in deletedIds.Distinct())
        {
            await _signalRBroadcastService.BroadcastStorageDeleteAsync(
                "activity",
                new StorageDeleteEvent("activity", deletedId)
            );
        }

        await _events.OnDeletedAsync(null, CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task<long> CountActivitiesAsync(
        string? find = null,
        CancellationToken cancellationToken = default
    )
    {
        var counts = await CountActivitiesByCategoryAsync(
            CountableCategories, find, cancellationToken);
        return counts.Values.Sum();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, long>> CountActivitiesByCategoryAsync(
        IReadOnlySet<string> categories,
        string? find = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug(
                "Counting activity records in {Categories} with find: {Find}",
                string.Join(",", categories),
                LogText.Sanitize(find));

            var query = FindQuery.Parse(find);
            var structured = !query.IsEmpty;
            var from = query.FromMills is { } fromMills
                ? DateTimeOffset.FromUnixTimeMilliseconds(fromMills).UtcDateTime
                : (DateTime?)null;
            var to = query.ToMills is { } toMills
                ? DateTimeOffset.FromUnixTimeMilliseconds(toMills).UtcDateTime
                : (DateTime?)null;

            var fetchCount = query.HasFieldFilters ? MaxFilteredFetch : int.MaxValue;

            // Sequential: the services share one DbContext.
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            if (categories.Contains(Scope.TreatmentsRead))
            {
                var stateSpans = await _stateSpanService.GetActivitiesAsync(
                    count: fetchCount,
                    skip: 0,
                    from: from,
                    to: to,
                    cancellationToken: cancellationToken);
                counts[Scope.TreatmentsRead] = Narrow(stateSpans, query).Count();
            }

            if (categories.Contains(Scope.HeartRateRead))
            {
                var heartRates = await _heartRateService.GetHeartRatesAsync(
                    count: fetchCount,
                    skip: 0,
                    from: from,
                    to: to,
                    cancellationToken: cancellationToken);
                counts[Scope.HeartRateRead] = query.HasFieldFilters
                    ? Narrow(heartRates.Select(ActivityDecomposer.HeartRateToActivity), query).Count()
                    : heartRates.Count();
            }

            if (categories.Contains(Scope.StepCountRead))
            {
                var stepCounts = await _stepCountService.GetStepCountsAsync(
                    count: fetchCount,
                    skip: 0,
                    from: from,
                    to: to,
                    cancellationToken: cancellationToken);
                counts[Scope.StepCountRead] = query.HasFieldFilters
                    ? Narrow(stepCounts.Select(ActivityDecomposer.StepCountToActivity), query).Count()
                    : stepCounts.Count();
            }

            if (categories.Contains(Scope.SleepRead))
            {
                if (!structured)
                    counts[Scope.SleepRead] = await _sleepService.CountSessionsAsync(cancellationToken: cancellationToken);
                else
                {
                    var sessions = await _sleepService.GetSessionsAsync(
                        from: from,
                        to: to,
                        limit: fetchCount,
                        offset: 0,
                        descending: true,
                        cancellationToken: cancellationToken);
                    counts[Scope.SleepRead] = Narrow(
                        sessions.Select(ActivityStateSpanMapper.SleepSessionToActivity), query).Count();
                }
            }

            _logger.LogDebug("Counted {Total} activity records", counts.Values.Sum());
            return counts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error counting activity records");
            throw;
        }
    }

    /// <summary>The find bounds every activity by its start, though the sleep read bounds by overlap.</summary>
    private static bool StartsWithin(Activity activity, FindQuery query) =>
        (query.FromMills is not { } lower || activity.Mills >= lower)
        && (query.ToMills is not { } upper || activity.Mills <= upper);

    private static IEnumerable<Activity> Narrow(IEnumerable<Activity> activities, FindQuery query)
    {
        var bounded = activities.Where(a => StartsWithin(a, query));
        return query.HasFieldFilters ? bounded.Where(a => query.MatchesResidual(a)) : bounded;
    }
}
