using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services.ConnectorPublishing;

public class InProcessConnectorPublisher : IConnectorPublisher
{
    private const string DefaultUserId = "default";

    private readonly IEntryService _entryService;
    private readonly ITreatmentService _treatmentService;
    private readonly IDeviceStatusService _deviceStatusService;
    private readonly IProfileDataService _profileDataService;
    private readonly IFoodService _foodService;
    private readonly IConnectorFoodEntryService _connectorFoodEntryService;
    private readonly IActivityService _activityService;
    private readonly IStateSpanService _stateSpanService;
    private readonly SystemEventRepository _systemEventRepository;
    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly IBolusRepository _bolusRepository;
    private readonly ICarbIntakeRepository _carbIntakeRepository;
    private readonly IBGCheckRepository _bgCheckRepository;
    private readonly IBolusCalculationRepository _bolusCalculationRepository;
    private readonly INoteRepository _noteRepository;
    private readonly IDeviceEventRepository _deviceEventRepository;
    private readonly ILogger<InProcessConnectorPublisher> _logger;

    public InProcessConnectorPublisher(
        IEntryService entryService,
        ITreatmentService treatmentService,
        IDeviceStatusService deviceStatusService,
        IProfileDataService profileDataService,
        IFoodService foodService,
        IConnectorFoodEntryService connectorFoodEntryService,
        IActivityService activityService,
        IStateSpanService stateSpanService,
        SystemEventRepository systemEventRepository,
        ISensorGlucoseRepository sensorGlucoseRepository,
        IBolusRepository bolusRepository,
        ICarbIntakeRepository carbIntakeRepository,
        IBGCheckRepository bgCheckRepository,
        IBolusCalculationRepository bolusCalculationRepository,
        INoteRepository noteRepository,
        IDeviceEventRepository deviceEventRepository,
        ILogger<InProcessConnectorPublisher> logger
    )
    {
        _entryService = entryService ?? throw new ArgumentNullException(nameof(entryService));
        _treatmentService =
            treatmentService ?? throw new ArgumentNullException(nameof(treatmentService));
        _deviceStatusService =
            deviceStatusService ?? throw new ArgumentNullException(nameof(deviceStatusService));
        _profileDataService =
            profileDataService ?? throw new ArgumentNullException(nameof(profileDataService));
        _foodService = foodService ?? throw new ArgumentNullException(nameof(foodService));
        _connectorFoodEntryService =
            connectorFoodEntryService
            ?? throw new ArgumentNullException(nameof(connectorFoodEntryService));
        _activityService =
            activityService ?? throw new ArgumentNullException(nameof(activityService));
        _stateSpanService =
            stateSpanService ?? throw new ArgumentNullException(nameof(stateSpanService));
        _systemEventRepository =
            systemEventRepository ?? throw new ArgumentNullException(nameof(systemEventRepository));
        _sensorGlucoseRepository =
            sensorGlucoseRepository
            ?? throw new ArgumentNullException(nameof(sensorGlucoseRepository));
        _bolusRepository =
            bolusRepository ?? throw new ArgumentNullException(nameof(bolusRepository));
        _carbIntakeRepository =
            carbIntakeRepository ?? throw new ArgumentNullException(nameof(carbIntakeRepository));
        _bgCheckRepository =
            bgCheckRepository ?? throw new ArgumentNullException(nameof(bgCheckRepository));
        _bolusCalculationRepository =
            bolusCalculationRepository
            ?? throw new ArgumentNullException(nameof(bolusCalculationRepository));
        _noteRepository = noteRepository ?? throw new ArgumentNullException(nameof(noteRepository));
        _deviceEventRepository =
            deviceEventRepository ?? throw new ArgumentNullException(nameof(deviceEventRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsAvailable => true;

    public async Task<bool> PublishEntriesAsync(
        IEnumerable<Entry> entries,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await _entryService.CreateEntriesAsync(entries, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish entries for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await _treatmentService.CreateTreatmentsAsync(treatments, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish treatments for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishDeviceStatusAsync(
        IEnumerable<DeviceStatus> deviceStatuses,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await _deviceStatusService.CreateDeviceStatusAsync(deviceStatuses, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish device status for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishProfilesAsync(
        IEnumerable<Profile> profiles,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await _profileDataService.CreateProfilesAsync(profiles, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish profiles for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishFoodAsync(
        IEnumerable<Food> foods,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await _foodService.CreateFoodAsync(foods, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish food for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishConnectorFoodEntriesAsync(
        IEnumerable<ConnectorFoodEntryImport> entries,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await _connectorFoodEntryService.ImportAsync(DefaultUserId, entries, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish connector food entries for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishActivityAsync(
        IEnumerable<Activity> activities,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await _activityService.CreateActivitiesAsync(activities, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish activities for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishStateSpansAsync(
        IEnumerable<StateSpan> stateSpans,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            foreach (var span in stateSpans)
            {
                await _stateSpanService.UpsertStateSpanAsync(span, cancellationToken);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish state spans for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishSystemEventsAsync(
        IEnumerable<SystemEvent> systemEvents,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await _systemEventRepository.BulkUpsertAsync(systemEvents, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish system events for {Source}", source);
            return false;
        }
    }

    public async Task<DateTime?> GetLatestEntryTimestampAsync(
        string source,
        CancellationToken cancellationToken = default
    )
    {
        // TODO: Filter by source to support multi-connector catch-up. Currently returns global latest.
        var entry = await _entryService.GetCurrentEntryAsync(cancellationToken);
        if (entry == null)
            return null;

        if (entry.Date != default)
            return entry.Date;

        if (entry.Mills > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(entry.Mills).UtcDateTime;

        return null;
    }

    public async Task<DateTime?> GetLatestTreatmentTimestampAsync(
        string source,
        CancellationToken cancellationToken = default
    )
    {
        // TODO: Filter by source to support multi-connector catch-up. Currently returns global latest.
        var latest = (
            await _treatmentService.GetTreatmentsAsync(
                count: 1,
                skip: 0,
                cancellationToken: cancellationToken
            )
        ).FirstOrDefault();

        if (latest == null)
            return null;

        if (
            !string.IsNullOrEmpty(latest.CreatedAt)
            && DateTime.TryParse(latest.CreatedAt, out var createdAt)
        )
            return createdAt;

        if (latest.Mills > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(latest.Mills).UtcDateTime;

        return null;
    }

    #region V4 Publishing Methods

    public async Task<bool> PublishSensorGlucoseAsync(
        IEnumerable<SensorGlucose> records,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0)
                return true;

            await _sensorGlucoseRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug(
                "Published {Count} SensorGlucose records for {Source}",
                recordList.Count,
                source
            );
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish SensorGlucose records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishBolusesAsync(
        IEnumerable<Bolus> records,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0)
                return true;

            await _bolusRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug(
                "Published {Count} Bolus records for {Source}",
                recordList.Count,
                source
            );
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish Bolus records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishCarbIntakesAsync(
        IEnumerable<CarbIntake> records,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0)
                return true;

            await _carbIntakeRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug(
                "Published {Count} CarbIntake records for {Source}",
                recordList.Count,
                source
            );
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish CarbIntake records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishBGChecksAsync(
        IEnumerable<BGCheck> records,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0)
                return true;

            await _bgCheckRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug(
                "Published {Count} BGCheck records for {Source}",
                recordList.Count,
                source
            );
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish BGCheck records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishBolusCalculationsAsync(
        IEnumerable<BolusCalculation> records,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0)
                return true;

            await _bolusCalculationRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug(
                "Published {Count} BolusCalculation records for {Source}",
                recordList.Count,
                source
            );
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish BolusCalculation records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishNotesAsync(
        IEnumerable<Note> records,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0)
                return true;

            await _noteRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug(
                "Published {Count} Note records for {Source}",
                recordList.Count,
                source
            );
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish Note records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishDeviceEventsAsync(
        IEnumerable<DeviceEvent> records,
        string source,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0)
                return true;

            await _deviceEventRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug(
                "Published {Count} DeviceEvent records for {Source}",
                recordList.Count,
                source
            );
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish DeviceEvent records for {Source}", source);
            return false;
        }
    }

    public async Task<DateTime?> GetLatestSensorGlucoseTimestampAsync(
        string source,
        CancellationToken cancellationToken = default
    )
    {
        return await _sensorGlucoseRepository.GetLatestTimestampAsync(source, cancellationToken);
    }

    #endregion
}
