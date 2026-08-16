using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
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
    /// Every source <see cref="CountActivitiesByCategoryAsync"/> knows how to count, named by the
    /// read scope its records carry.
    /// </summary>
    private static readonly IReadOnlySet<string> CountableCategories = new HashSet<string>(
        StringComparer.Ordinal)
    {
        OAuthScopes.TreatmentsRead,
        OAuthScopes.HeartRateRead,
        OAuthScopes.StepCountRead,
        OAuthScopes.SleepRead,
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

            // Over-fetch from each source so we can merge and re-paginate. Clamped into range: a
            // large skip overflows the sum, and a non-positive fetch count faults every source
            // query. Callers with no ceiling of their own rely on the upper bound here.
            var fetchCount = (int)Math.Clamp((long)actualCount + actualSkip, 1, MaxOverFetch);

            // Source 1: Regular activities from StateSpans (exercise, illness, travel — no longer sleep)
            var stateSpanActivities = await _stateSpanService.GetActivitiesAsync(
                type: find,
                count: fetchCount,
                skip: 0,
                cancellationToken: cancellationToken
            );

            // Source 2: Heart rate records converted to Activity format
            var heartRates = await _heartRateService.GetHeartRatesAsync(
                count: fetchCount,
                skip: 0,
                cancellationToken: cancellationToken
            );
            var heartRateActivities = heartRates.Select(ActivityDecomposer.HeartRateToActivity);

            // Source 3: Step count records converted to Activity format
            var stepCounts = await _stepCountService.GetStepCountsAsync(
                count: fetchCount,
                skip: 0,
                cancellationToken: cancellationToken
            );
            var stepCountActivities = stepCounts.Select(ActivityDecomposer.StepCountToActivity);

            // Source 4: Sleep sessions projected back to Activity format.
            // Sleep used to be a StateSpan filtered by `find`; honour that filter here
            // so a request scoped to another type (e.g. exercise) doesn't pull in sleep.
            var sleepActivities = Enumerable.Empty<Activity>();
            if (string.IsNullOrEmpty(find) || ActivityStateSpanMapper.IsSleepType(find))
            {
                var sleepSessions = await _sleepService.GetSessionsAsync(
                    limit: fetchCount,
                    offset: 0,
                    descending: true,
                    cancellationToken: cancellationToken
                );
                sleepActivities = sleepSessions.Select(ActivityStateSpanMapper.SleepSessionToActivity);
            }

            // Merge all sources, sort by Mills descending, apply pagination
            var merged = stateSpanActivities
                .Concat(heartRateActivities)
                .Concat(stepCountActivities)
                .Concat(sleepActivities)
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
        // Sequential: the three services share one DbContext. Max over DateTime? skips the
        // destinations this source has never written to, and is null when that is all of them.
        var candidates = new[]
        {
            await _stateSpanService.GetLatestActivityTimestampAsync(source, cancellationToken),
            await _heartRateService.GetLatestTimestampAsync(source, cancellationToken),
            await _stepCountService.GetLatestTimestampAsync(source, cancellationToken),
        };

        return candidates.Max();
    }

    /// <inheritdoc />
    public async Task<Activity?> GetActivityByIdAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Getting activity record by ID: {Id}", id);

            // Try StateSpan first
            var activity = await _stateSpanService.GetActivityByIdAsync(id, cancellationToken);
            if (activity != null)
                return activity;

            // Try sleep session
            if (Guid.TryParse(id, out var sleepGuid))
            {
                var sleepSession = await _sleepService.GetSessionByIdAsync(sleepGuid, cancellationToken);
                if (sleepSession != null)
                    return ActivityStateSpanMapper.SleepSessionToActivity(sleepSession);
            }

            // Try heart rate
            var heartRate = await _heartRateService.GetHeartRateByIdAsync(id, cancellationToken);
            if (heartRate != null)
                return ActivityDecomposer.HeartRateToActivity(heartRate);

            // Try step count
            var stepCount = await _stepCountService.GetStepCountByIdAsync(id, cancellationToken);
            if (stepCount != null)
                return ActivityDecomposer.StepCountToActivity(stepCount);

            return null;
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
            var activityList = activities.ToList();
            _logger.LogDebug("Creating {Count} activity records", activityList.Count);

            // Process documents (sanitization and timestamp conversion)
            var processedActivities = _documentProcessingService.ProcessDocuments(activityList);
            var processedList = processedActivities.ToList();

            // Separate sensor data, sleep activities, and regular activities
            var regularActivities = new List<Activity>();
            var sensorDataActivities = new List<Activity>();
            var sleepActivities = new List<Activity>();

            foreach (var activity in processedList)
            {
                if (_activityDecomposer.IsSensorData(activity))
                    sensorDataActivities.Add(activity);
                else if (ActivityStateSpanMapper.IsSleepType(activity.Type))
                    sleepActivities.Add(activity);
                else
                    regularActivities.Add(activity);
            }

            var results = new List<Activity>();

            // Process sensor data through decomposer (NOT stored as StateSpans)
            foreach (var sensorActivity in sensorDataActivities)
            {
                try
                {
                    await _activityDecomposer.DecomposeAsync(sensorActivity, WriteOrigin.Live, cancellationToken);
                    results.Add(sensorActivity);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to decompose sensor data activity {Id}",
                        sensorActivity.Id
                    );
                }
            }

            // Route sleep-type activities to the dedicated sleep_sessions table
            foreach (var sleepActivity in sleepActivities)
            {
                try
                {
                    var session = ActivityStateSpanMapper.ToSleepSession(sleepActivity);
                    var created = await _sleepService.UpsertSessionAsync(session, cancellationToken);
                    results.Add(ActivityStateSpanMapper.SleepSessionToActivity(created));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Mirror the sensor-data branch: log and skip the failed record
                    // rather than failing the whole batch. Covers the rare upsert
                    // unique-constraint conflict (concurrent sync of the same record).
                    _logger.LogError(
                        ex,
                        "Failed to create sleep session from activity {Id}",
                        sleepActivity.Id
                    );
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
            }

            // Broadcast WebSocket event for all created activities
            if (results.Count > 0)
            {
                await _signalRBroadcastService.BroadcastStorageCreateAsync(
                    "activity",
                    new { collection = "activity", data = results, count = results.Count }
                );

                await _events.OnCreatedAsync(results, cancellationToken);
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
            if (ActivityStateSpanMapper.IsSleepType(activity.Type))
            {
                var sleepSession = ActivityStateSpanMapper.ToSleepSession(activity);
                sleepSession.OriginalId = id;

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating activity record with ID: {Id}", id);
            throw;
        }
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

            // Attempt to delete decomposed records (heart rate / step count)
            try
            {
                await _activityDecomposer.DeleteByLegacyIdAsync(id, WriteOrigin.Live, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to delete decomposed records for legacy activity {Id}",
                    id
                );
            }

            // Try deleting from sleep sessions
            if (Guid.TryParse(id, out var sleepGuid))
            {
                var sleepDeleted = await _sleepService.DeleteSessionAsync(sleepGuid, cancellationToken);
                if (sleepDeleted)
                {
                    await _signalRBroadcastService.BroadcastStorageDeleteAsync(
                        "activity",
                        new { collection = "activity", id }
                    );
                    await _events.OnDeletedAsync(null, cancellationToken);
                    _logger.LogDebug("Successfully deleted sleep session for activity ID: {Id}", id);
                    return true;
                }
            }

            var deleted = await _stateSpanService.DeleteActivityAsync(id, cancellationToken);

            if (deleted)
            {
                await _signalRBroadcastService.BroadcastStorageDeleteAsync(
                    "activity",
                    new { collection = "activity", id = id }
                );

                await _events.OnDeletedAsync(null, cancellationToken);

                _logger.LogDebug("Successfully deleted activity record with ID: {Id}", id);
            }

            return deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting activity record with ID: {Id}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<long> DeleteMultipleActivitiesAsync(
        string? find = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug("Bulk deleting activity records with filter: {Find}", find);

            // Regular activities are stored as StateSpans and are the only source
            // addressable by the type-based `find` filter. Heart rate and step count
            // sensor data live in dedicated tables and are not bulk-deletable here.
            var activities = await _stateSpanService.GetActivitiesAsync(
                type: find,
                count: int.MaxValue,
                skip: 0,
                cancellationToken: cancellationToken
            );

            long deletedCount = 0;
            foreach (var activity in activities)
            {
                if (string.IsNullOrEmpty(activity.Id))
                    continue;

                // Clean up any decomposed records linked to this legacy activity id.
                try
                {
                    await _activityDecomposer.DeleteByLegacyIdAsync(
                        activity.Id,
                        WriteOrigin.Live,
                        cancellationToken
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to delete decomposed records for legacy activity {Id}",
                        activity.Id
                    );
                }

                if (await _stateSpanService.DeleteActivityAsync(activity.Id, cancellationToken))
                    deletedCount++;
            }

            if (deletedCount > 0)
            {
                await _signalRBroadcastService.BroadcastStorageDeleteAsync(
                    "activity",
                    new { collection = "activity", count = deletedCount }
                );

                await _events.OnBulkDeletedAsync(deletedCount, cancellationToken);

                _logger.LogDebug(
                    "Successfully bulk deleted {Count} activity records",
                    deletedCount
                );
            }

            return deletedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk deleting activity records");
            throw;
        }
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
            var sanitizedFindForLog = find?.Replace("\r", string.Empty).Replace("\n", string.Empty);

            _logger.LogDebug(
                "Counting activity records in {Categories} with find: {Find}",
                string.Join(",", categories),
                sanitizedFindForLog);

            // Sleep sessions are merged into GetActivitiesAsync only when `find` is
            // empty or a sleep type; the count applies the same gate
            var countSleep = string.IsNullOrEmpty(find) || ActivityStateSpanMapper.IsSleepType(find);

            var pending = new Dictionary<string, Task<long>>(StringComparer.Ordinal);
            if (categories.Contains(OAuthScopes.TreatmentsRead))
                pending[OAuthScopes.TreatmentsRead] = CountOf(_stateSpanService.GetActivitiesAsync(
                    type: find,
                    count: int.MaxValue,
                    skip: 0,
                    cancellationToken: cancellationToken));

            if (categories.Contains(OAuthScopes.HeartRateRead))
                pending[OAuthScopes.HeartRateRead] = CountOf(_heartRateService.GetHeartRatesAsync(
                    count: int.MaxValue,
                    skip: 0,
                    cancellationToken: cancellationToken));

            if (categories.Contains(OAuthScopes.StepCountRead))
                pending[OAuthScopes.StepCountRead] = CountOf(_stepCountService.GetStepCountsAsync(
                    count: int.MaxValue,
                    skip: 0,
                    cancellationToken: cancellationToken));

            if (categories.Contains(OAuthScopes.SleepRead))
                pending[OAuthScopes.SleepRead] = countSleep
                    ? Widen(_sleepService.CountSessionsAsync(cancellationToken: cancellationToken))
                    : Task.FromResult(0L);

            await Task.WhenAll(pending.Values);

            var counts = pending.ToDictionary(
                source => source.Key, source => source.Value.Result, StringComparer.Ordinal);

            _logger.LogDebug("Counted {Total} activity records", counts.Values.Sum());
            return counts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error counting activity records");
            throw;
        }

        static async Task<long> CountOf<T>(Task<IEnumerable<T>> source) => (await source).Count();

        static async Task<long> Widen(Task<int> source) => await source;
    }
}
