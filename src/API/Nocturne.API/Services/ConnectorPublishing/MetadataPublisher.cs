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
        CancellationToken cancellationToken = default)
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
        CancellationToken cancellationToken = default)
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
        CancellationToken cancellationToken = default)
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
        CancellationToken cancellationToken = default)
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
        CancellationToken cancellationToken = default)
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
        CancellationToken cancellationToken = default)
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
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            await _noteRepository.BulkCreateAsync(recordList, cancellationToken);
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
}
