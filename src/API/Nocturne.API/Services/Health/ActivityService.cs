using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Health;

/// <summary>
/// Domain service implementation for <see cref="Activity"/> operations with WebSocket broadcasting.
/// Regular activities are stored as <see cref="StateSpan"/> records via <see cref="IStateSpanService"/>.
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
    private readonly IDocumentProcessingService _documentProcessingService;
    private readonly ISignalRBroadcastService _signalRBroadcastService;
    private readonly IDataEventSink<Activity> _events;
    private readonly IActivityDecomposer _activityDecomposer;
    private readonly IHeartRateService _heartRateService;
    private readonly IStepCountService _stepCountService;
    private readonly ILogger<ActivityService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ActivityService"/>.
    /// </summary>
    /// <param name="stateSpanService">Service for persisting regular activities as <see cref="StateSpan"/> records.</param>
    /// <param name="documentProcessingService">Service for HTML sanitization of activity fields.</param>
    /// <param name="signalRBroadcastService">Service for broadcasting real-time updates to connected clients.</param>
    /// <param name="events">The event sink for broadcasting create/update/delete events.</param>
    /// <param name="activityDecomposer">Decomposes sensor data activities into heart rate and step count records.</param>
    /// <param name="heartRateService">Service for reading and resolving heart rate records as activities.</param>
    /// <param name="stepCountService">Service for reading and resolving step count records as activities.</param>
    /// <param name="logger">The logger instance.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is <see langword="null"/>.</exception>
    public ActivityService(
        IStateSpanService stateSpanService,
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

            // Over-fetch from each source so we can merge and re-paginate
            var fetchCount = actualCount + actualSkip;

            // Source 1: Regular activities from StateSpans
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

            // Merge all sources, sort by Mills descending, apply pagination
            var merged = stateSpanActivities
                .Concat(heartRateActivities)
                .Concat(stepCountActivities)
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

            // Separate sensor data (heart rate, step count) from regular activities
            var regularActivities = new List<Activity>();
            var sensorDataActivities = new List<Activity>();

            foreach (var activity in processedList)
            {
                if (_activityDecomposer.IsSensorData(activity))
                    sensorDataActivities.Add(activity);
                else
                    regularActivities.Add(activity);
            }

            var results = new List<Activity>();

            // Process sensor data through decomposer (NOT stored as StateSpans)
            foreach (var sensorActivity in sensorDataActivities)
            {
                try
                {
                    await _activityDecomposer.DecomposeAsync(sensorActivity, cancellationToken);
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

            var updatedActivity = await _stateSpanService.UpdateActivityAsync(
                id,
                activity,
                cancellationToken
            );

            if (updatedActivity != null)
            {
                await _signalRBroadcastService.BroadcastStorageUpdateAsync(
                    "activity",
                    new { collection = "activity", data = updatedActivity, id = id }
                );

                await _events.OnUpdatedAsync(updatedActivity, cancellationToken);

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
                await _activityDecomposer.DeleteByLegacyIdAsync(id, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to delete decomposed records for legacy activity {Id}",
                    id
                );
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

            // TODO: Implement bulk delete for activities stored as StateSpans
            _logger.LogWarning("Bulk delete for activities is not implemented yet");
            return await Task.FromResult(0L);
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
        try
        {
            _logger.LogDebug("Counting activity records with find: {Find}", find);

            // Count from each decomposed source and sum
            var stateSpanTask = _stateSpanService.GetActivitiesAsync(
                type: find,
                count: int.MaxValue,
                skip: 0,
                cancellationToken: cancellationToken
            );

            var heartRateTask = _heartRateService.GetHeartRatesAsync(
                count: int.MaxValue,
                skip: 0,
                cancellationToken: cancellationToken
            );

            var stepCountTask = _stepCountService.GetStepCountsAsync(
                count: int.MaxValue,
                skip: 0,
                cancellationToken: cancellationToken
            );

            await Task.WhenAll(stateSpanTask, heartRateTask, stepCountTask);

            var total = stateSpanTask.Result.Count()
                + heartRateTask.Result.Count()
                + stepCountTask.Result.Count();

            _logger.LogDebug("Counted {Total} activity records", total);
            return total;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error counting activity records");
            throw;
        }
    }
}
