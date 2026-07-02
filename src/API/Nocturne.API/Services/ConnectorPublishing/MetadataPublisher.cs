using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Services.ConnectorPublishing;

/// <summary>
/// Publishes profile, food, activity, state-span, system event, and note data received from
/// connectors into the Nocturne domain via the appropriate service and repository interfaces.
/// </summary>
/// <seealso cref="IMetadataPublisher"/>
internal sealed class MetadataPublisher : IMetadataPublisher
{
    private const string DefaultUserId = "default";

    private readonly IProfileWriteService _profileWriteService;
    private readonly IFoodService _foodService;
    private readonly IConnectorFoodEntryService _connectorFoodEntryService;
    private readonly IActivityService _activityService;
    private readonly IStateSpanService _stateSpanService;
    private readonly ISystemEventRepository _systemEventRepository;
    private readonly INoteRepository _noteRepository;
    private readonly ILogger<MetadataPublisher> _logger;

    public MetadataPublisher(
        IProfileWriteService profileWriteService,
        IFoodService foodService,
        IConnectorFoodEntryService connectorFoodEntryService,
        IActivityService activityService,
        IStateSpanService stateSpanService,
        ISystemEventRepository systemEventRepository,
        INoteRepository noteRepository,
        ILogger<MetadataPublisher> logger)
    {
        _profileWriteService = profileWriteService ?? throw new ArgumentNullException(nameof(profileWriteService));
        _foodService = foodService ?? throw new ArgumentNullException(nameof(foodService));
        _connectorFoodEntryService = connectorFoodEntryService ?? throw new ArgumentNullException(nameof(connectorFoodEntryService));
        _activityService = activityService ?? throw new ArgumentNullException(nameof(activityService));
        _stateSpanService = stateSpanService ?? throw new ArgumentNullException(nameof(stateSpanService));
        _systemEventRepository = systemEventRepository ?? throw new ArgumentNullException(nameof(systemEventRepository));
        _noteRepository = noteRepository ?? throw new ArgumentNullException(nameof(noteRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> PublishProfilesAsync(
        IEnumerable<Profile> profiles,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            await _profileWriteService.CreateProfilesAsync(profiles, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish profiles for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishFoodAsync(
        IEnumerable<Food> foods,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            await _foodService.CreateFoodAsync(foods, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish food for {Source}", source);
            return false;
        }
    }

    public async Task<IReadOnlyList<ConnectorFoodEntry>?> PublishConnectorFoodEntriesAsync(
        IEnumerable<ConnectorFoodEntryImport> entries,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _connectorFoodEntryService.ImportAsync(
                DefaultUserId,
                entries,
                cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish connector food entries for {Source}", source);
            return null;
        }
    }

    public async Task<bool> PublishActivityAsync(
        IEnumerable<Activity> activities,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            await _activityService.CreateActivitiesAsync(activities, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish activities for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishStateSpansAsync(
        IEnumerable<StateSpan> stateSpans,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var span in stateSpans)
            {
                await _stateSpanService.UpsertStateSpanAsync(span, cancellationToken);
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish state spans for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishSystemEventsAsync(
        IEnumerable<SystemEvent> systemEvents,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            await _systemEventRepository.BulkUpsertAsync(systemEvents, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish system events for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishNotesAsync(
        IEnumerable<Note> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            await _noteRepository.BulkCreateAsync(recordList, origin, cancellationToken);
            _logger.LogDebug("Published {Count} Note records for {Source}", recordList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish Note records for {Source}", source);
            return false;
        }
    }

    /// <summary>
    /// Returns the timestamp of the most recent activity record for the current tenant,
    /// or <c>null</c> if none exist. Activities are stored across decomposed sources (StateSpans,
    /// HeartRate, StepCount); <see cref="IActivityService.GetActivitiesAsync"/> merges them and
    /// orders newest-first, so requesting a single record yields the global latest. Like
    /// <see cref="ITreatmentPublisher.GetLatestTreatmentTimestampAsync"/>, this is not source-filtered.
    /// </summary>
    public async Task<DateTime?> GetLatestActivityTimestampAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        // TODO: Filter by source to support multi-connector catch-up. Currently returns global latest.
        var latest = (await _activityService.GetActivitiesAsync(
                count: 1,
                skip: 0,
                cancellationToken: cancellationToken))
            .FirstOrDefault();

        if (latest == null)
            return null;

        if (!string.IsNullOrEmpty(latest.CreatedAt)
            && DateTime.TryParse(latest.CreatedAt, out var createdAt))
            return createdAt;

        if (latest.Mills > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(latest.Mills).UtcDateTime;

        return null;
    }
}
