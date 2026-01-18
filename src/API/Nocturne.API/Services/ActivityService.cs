using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Services;

/// <summary>
/// Domain service implementation for activity operations with WebSocket broadcasting.
/// Activities are stored as StateSpans under the hood for unified data management.
/// </summary>
public class ActivityService : IActivityService
{
    private readonly IStateSpanService _stateSpanService;
    private readonly IDocumentProcessingService _documentProcessingService;
    private readonly ISignalRBroadcastService _signalRBroadcastService;
    private readonly ILogger<ActivityService> _logger;

    public ActivityService(
        IStateSpanService stateSpanService,
        IDocumentProcessingService documentProcessingService,
        ISignalRBroadcastService signalRBroadcastService,
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
            _logger.LogDebug(
                "Getting activity records with find: {Find}, count: {Count}, skip: {Skip}",
                find,
                count,
                skip
            );

            return await _stateSpanService.GetActivitiesAsync(
                type: find,
                count: count ?? 10,
                skip: skip ?? 0,
                cancellationToken: cancellationToken
            );
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
            return await _stateSpanService.GetActivityByIdAsync(id, cancellationToken);
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
            var processedActivities = _documentProcessingService.ProcessDocuments(
                activityList
            );
            var processedList = processedActivities.ToList();

            // Create via StateSpanService (stored as StateSpans)
            var createdActivities = await _stateSpanService.CreateActivitiesAsync(
                processedList,
                cancellationToken
            );
            var resultList = createdActivities.ToList();

            // Broadcast WebSocket event for storage create
            await _signalRBroadcastService.BroadcastStorageCreateAsync(
                "activity",
                new
                {
                    collection = "activity",
                    data = resultList,
                    count = resultList.Count,
                }
            );

            _logger.LogDebug("Successfully created {Count} activity records", resultList.Count);
            return resultList;
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
                // Broadcast WebSocket event for storage update
                await _signalRBroadcastService.BroadcastStorageUpdateAsync(
                    "activity",
                    new
                    {
                        collection = "activity",
                        data = updatedActivity,
                        id = id,
                    }
                );

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

            var deleted = await _stateSpanService.DeleteActivityAsync(id, cancellationToken);

            if (deleted)
            {
                // Broadcast WebSocket event for storage delete
                await _signalRBroadcastService.BroadcastStorageDeleteAsync(
                    "activity",
                    new { collection = "activity", id = id }
                );

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
}
