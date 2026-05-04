using Nocturne.API.Services.Health;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// Domain service for <see cref="StateSpan"/> operations including
/// <see cref="Activity"/> compatibility methods that map activities to/from
/// <see cref="StateSpan"/> records via <see cref="ActivityStateSpanMapper"/>.
/// </summary>
/// <seealso cref="IStateSpanService"/>
/// <seealso cref="IStateSpanRepository"/>
/// <seealso cref="ActivityStateSpanMapper"/>
/// <seealso cref="ActivityService"/>
/// <seealso cref="StateSpanCategory"/>
public class StateSpanService : IStateSpanService
{
    private readonly IStateSpanRepository _repository;
    private readonly ILogger<StateSpanService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="StateSpanService"/>.
    /// </summary>
    /// <param name="repository">The state span repository for data access.</param>
    /// <param name="logger">The logger instance.</param>
    public StateSpanService(
        IStateSpanRepository repository,
        ILogger<StateSpanService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StateSpan>> GetStateSpansAsync(
        StateSpanCategory? category = null,
        string? state = null,
        DateTime? from = null,
        DateTime? to = null,
        string? source = null,
        bool? active = null,
        int count = 100,
        int skip = 0,
        bool descending = true,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Getting state spans with category: {Category}, state: {State}, from: {From}, to: {To}, source: {Source}, active: {Active}, count: {Count}, skip: {Skip}",
            category, state, from, to, source, active, count, skip);

        return await _repository.GetStateSpansAsync(
            category, state, from, to, source, active, count, skip, descending, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CountStateSpansAsync(
        StateSpanCategory? category = null,
        string? state = null,
        DateTime? from = null,
        DateTime? to = null,
        string? source = null,
        bool? active = null,
        CancellationToken cancellationToken = default)
    {
        return await _repository.CountStateSpansAsync(
            category, state, from, to, source, active, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StateSpan?> GetStateSpanByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting state span by ID: {Id}", id);

        return await _repository.GetStateSpanByIdAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StateSpan> UpsertStateSpanAsync(
        StateSpan stateSpan,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Upserting state span with OriginalId: {OriginalId}, Category: {Category}",
            stateSpan.OriginalId, stateSpan.Category);

        return await _repository.UpsertStateSpanAsync(stateSpan, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteStateSpanAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting state span with ID: {Id}", id);

        return await _repository.DeleteStateSpanAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StateSpan?> UpdateStateSpanAsync(
        string id,
        StateSpan stateSpan,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Updating state span with ID: {Id}, Category: {Category}",
            id, stateSpan.Category);

        return await _repository.UpdateStateSpanAsync(id, stateSpan, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StateSpan?> GetActiveAtAsync(
        StateSpanCategory category,
        string? state,
        DateTime at,
        CancellationToken cancellationToken = default)
    {
        return await _repository.GetActiveAtAsync(category, state, at, cancellationToken);
    }

    #region Activity Compatibility Methods

    /// <inheritdoc />
    public async Task<IEnumerable<Activity>> GetActivitiesAsync(
        string? type = null,
        int count = 10,
        int skip = 0,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Getting activities with type: {Type}, count: {Count}, skip: {Skip}",
            type, count, skip);

        // Get Activity StateSpans from repository
        var stateSpans = await _repository.GetActivityStateSpansAsync(
            type: type,
            count: count,
            skip: skip,
            cancellationToken: cancellationToken);

        // Convert each StateSpan to an Activity using the mapper
        var activities = stateSpans
            .Select(ActivityStateSpanMapper.ToActivity)
            .Where(a => a != null)
            .Cast<Activity>()
            .ToList();

        _logger.LogDebug("Converted {Count} state spans to activities", activities.Count);

        return activities;
    }

    /// <inheritdoc />
    public async Task<Activity?> GetActivityByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting activity by ID: {Id}", id);

        var stateSpan = await _repository.GetActivityStateSpanByIdAsync(id, cancellationToken);

        if (stateSpan == null)
            return null;

        return ActivityStateSpanMapper.ToActivity(stateSpan);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Activity>> CreateActivitiesAsync(
        IEnumerable<Activity> activities,
        CancellationToken cancellationToken = default)
    {
        var activityList = activities.ToList();
        _logger.LogDebug("Creating {Count} activities", activityList.Count);

        // Convert activities to StateSpans
        var stateSpans = activityList.Select(ActivityStateSpanMapper.ToStateSpan).ToList();

        // Create the StateSpans
        var createdSpans = await _repository.CreateActivitiesAsStateSpansAsync(
            stateSpans,
            cancellationToken);

        // Convert back to activities
        var createdActivities = createdSpans
            .Select(ActivityStateSpanMapper.ToActivity)
            .Where(a => a != null)
            .Cast<Activity>()
            .ToList();

        _logger.LogDebug("Successfully created {Count} activities", createdActivities.Count);

        return createdActivities;
    }

    /// <inheritdoc />
    public async Task<Activity?> UpdateActivityAsync(
        string id,
        Activity activity,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Updating activity with ID: {Id}", id);

        // Convert activity to StateSpan
        var stateSpan = ActivityStateSpanMapper.ToStateSpan(activity);

        // Update the StateSpan
        var updatedSpan = await _repository.UpdateActivityStateSpanAsync(
            id,
            stateSpan,
            cancellationToken);

        if (updatedSpan == null)
            return null;

        _logger.LogDebug("Successfully updated activity with ID: {Id}", id);

        return ActivityStateSpanMapper.ToActivity(updatedSpan);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteActivityAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting activity with ID: {Id}", id);

        var deleted = await _repository.DeleteActivityStateSpanAsync(id, cancellationToken);

        if (deleted)
            _logger.LogDebug("Successfully deleted activity with ID: {Id}", id);

        return deleted;
    }

    #endregion
}
