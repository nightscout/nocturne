using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Tests.Shared.Connectors;

/// <summary>
///     A connector publisher that keeps what it is given the way the store does: a record whose type
///     and key are already held is skipped, so a second sync of one payload adds nothing only while
///     every record keeps its key. The key is the first of the legacy id, sync identifier, original
///     id and external entry id a record type carries (device events match on the sync identifier,
///     state spans on the original id and connector food entries on the external entry id, for
///     example). A record of such a type with no key has nothing to
///     skip on and is listed in <see cref="Unkeyed"/>. A record type with none of the three (entries
///     and profiles, for example) is only counted in <see cref="NotDeduplicated"/>, because its real
///     key is not modelled. A patient device is keyed on its <see cref="PatientDevice.Id"/>, the way
///     the store upserts it, and one without an id is unkeyed. <see cref="Offered"/> counts every
///     record handed over, kept or skipped, so a sync that fetched nothing is not mistaken for one
///     that skipped everything. The connector's state between runs is <see cref="State"/>, an
///     <see cref="InMemoryConnectorStateStore"/> unless the test hands it another.
/// </summary>
public sealed class StoringConnectorPublisher(IConnectorStateStore? state = null) :
    IConnectorPublisher, IGlucosePublisher, ITreatmentPublisher, IDevicePublisher, IMetadataPublisher
{
    private readonly Dictionary<(string Type, string Key), object> _stored = [];
    private readonly List<object> _unkeyed = [];
    private readonly Dictionary<string, int> _notDeduplicated = [];
    private readonly Dictionary<string, int> _offered = [];
    private readonly object _gate = new();

    public bool IsAvailable => true;
    public IGlucosePublisher Glucose => this;
    public ITreatmentPublisher Treatments => this;
    public IDevicePublisher Device => this;
    public IMetadataPublisher Metadata => this;
    public IConnectorStateStore State { get; } = state ?? new InMemoryConnectorStateStore();
    public int SkippedDeleted => 0;
    public int Refused => 0;

    /// <summary>The rows the store would hold, keyed by record type name and key.</summary>
    public IReadOnlyDictionary<(string Type, string Key), object> Stored
    {
        get
        {
            lock (_gate) return new Dictionary<(string, string), object>(_stored);
        }
    }

    public IReadOnlyList<object> Unkeyed
    {
        get
        {
            lock (_gate) return [.. _unkeyed];
        }
    }

    public IReadOnlyDictionary<string, int> NotDeduplicated
    {
        get
        {
            lock (_gate) return new Dictionary<string, int>(_notDeduplicated);
        }
    }

    /// <summary>How many records of each type were offered, whether or not the store kept them.</summary>
    public IReadOnlyDictionary<string, int> Offered
    {
        get
        {
            lock (_gate) return new Dictionary<string, int>(_offered);
        }
    }

    public int StoredCount => Stored.Count;

    private static readonly string[] KeyProperties = ["LegacyId", "SyncIdentifier", "OriginalId", "ExternalEntryId"];

    private Task<bool> Keep<T>(IEnumerable<T> records) => Task.FromResult(Add(records) >= 0);

    /// <returns>The number of records newly stored.</returns>
    private int Add<T>(IEnumerable<T> records)
    {
        var keyProperties = KeyProperties
            .Select(name => typeof(T).GetProperty(name))
            .OfType<System.Reflection.PropertyInfo>()
            .ToList();
        var added = 0;

        lock (_gate)
        {
            foreach (var record in records)
            {
                _offered[typeof(T).Name] = _offered.GetValueOrDefault(typeof(T).Name) + 1;

                string? key;
                if (record is PatientDevice device)
                    key = device.Id == Guid.Empty ? null : device.Id.ToString();
                else if (keyProperties.Count == 0)
                {
                    _notDeduplicated[typeof(T).Name] = _notDeduplicated.GetValueOrDefault(typeof(T).Name) + 1;
                    continue;
                }
                else
                    key = keyProperties
                        .Select(property => property.GetValue(record) as string)
                        .FirstOrDefault(value => !string.IsNullOrEmpty(value));

                if (key is null)
                    _unkeyed.Add(record!);
                else if (_stored.TryAdd((typeof(T).Name, key), record!))
                    added++;
            }
        }

        return added;
    }

    private Task<int?> KeepCounting<T>(IEnumerable<T> records) => Task.FromResult<int?>(Add(records));

    public Task<bool> PublishEntriesAsync(IEnumerable<Entry> entries, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(entries);
    public Task<int?> PublishRecentEntriesAsync(IEnumerable<Entry> entries, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => KeepCounting(entries);
    public Task<bool> PublishSensorGlucoseAsync(IEnumerable<SensorGlucose> records, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(records);
    public Task<DateTime?> GetLatestEntryTimestampAsync(string source, CancellationToken cancellationToken = default) => Task.FromResult<DateTime?>(null);
    public Task<DateTime?> GetLatestSensorGlucoseTimestampAsync(string source, CancellationToken cancellationToken = default) => Task.FromResult<DateTime?>(null);

    public Task<bool> PublishTreatmentsAsync(IEnumerable<Treatment> treatments, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(treatments);
    public Task<bool> PublishBolusesAsync(IEnumerable<Bolus> records, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(records);
    public Task<bool> PublishCarbIntakesAsync(IEnumerable<CarbIntake> records, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(records);
    public Task<bool> PublishBGChecksAsync(IEnumerable<BGCheck> records, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(records);
    public Task<bool> PublishBolusCalculationsAsync(IEnumerable<BolusCalculation> records, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(records);
    public Task<bool> PublishTempBasalsAsync(IEnumerable<TempBasal> records, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(records);
    public Task<bool> PublishBasalInjectionsAsync(IEnumerable<BasalInjection> records, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(records);
    public Task<DateTime?> GetLatestTreatmentTimestampAsync(string source, CancellationToken cancellationToken = default) => Task.FromResult<DateTime?>(null);
    public Task<DateTime?> GetLatestTreatmentTimestampAsync(SyncDataType type, string source, CancellationToken cancellationToken = default) => Task.FromResult<DateTime?>(null);
    public Task<IReadOnlyList<StoredTreatmentKey>> GetOpenSpanTreatmentsAsync(string source, StoredTreatmentKey? after, int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StoredTreatmentKey>>([]);
    public Task<IReadOnlyDictionary<string, DateTime>> GetStoredTreatmentIdsAsync(string source, DateTime from, DateTime to, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, DateTime>>(new Dictionary<string, DateTime>());
    public Task<IReadOnlyDictionary<string, Guid>> GetStoredCarbIntakeIdsAsync(IReadOnlyCollection<string> legacyIds, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyDictionary<string, Guid>>(
                legacyIds
                    .Select(id => _stored.TryGetValue((nameof(CarbIntake), id), out var stored) ? (Id: id, Stored: stored as CarbIntake) : default)
                    .Where(found => found.Stored is not null)
                    .ToDictionary(found => found.Id, found => found.Stored!.Id));
    }
    public Task<int?> PublishRecentTreatmentsAsync(IEnumerable<Treatment> treatments, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => KeepCounting(treatments);
    public Task<int> DeleteTreatmentsAsync(string source, IReadOnlySet<string> legacyIds, CancellationToken cancellationToken = default) => Task.FromResult(0);

    public Task<bool> PublishDeviceStatusAsync(IEnumerable<DeviceStatus> deviceStatuses, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(deviceStatuses);
    public Task<int?> PublishRecentDeviceStatusAsync(IEnumerable<DeviceStatus> deviceStatuses, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => KeepCounting(deviceStatuses);
    public Task<bool> PublishDeviceEventsAsync(IEnumerable<DeviceEvent> records, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(records);
    public Task<bool> PublishPatientDevicesAsync(IEnumerable<PatientDevice> devices, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(devices);
    public Task<DateTime?> GetLatestDeviceStatusTimestampAsync(string source, CancellationToken cancellationToken = default) => Task.FromResult<DateTime?>(null);

    public Task<bool> PublishProfilesAsync(IEnumerable<Profile> profiles, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(profiles);
    public Task<bool> PublishFoodAsync(IEnumerable<Food> foods, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(foods);
    public Task<IReadOnlyList<ConnectorFoodEntry>?> PublishConnectorFoodEntriesAsync(IEnumerable<ConnectorFoodEntryImport> entries, string source, WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        Add(entries);
        return Task.FromResult<IReadOnlyList<ConnectorFoodEntry>?>([]);
    }
    public Task<int?> ReconcileConnectorFoodEntriesAsync(IEnumerable<string> presentExternalEntryIds, DateTimeOffset from, DateTimeOffset to, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Task.FromResult<int?>(0);
    public Task<bool> PublishActivityAsync(IEnumerable<Activity> activities, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(activities);
    public Task<int?> PublishRecentActivityAsync(IEnumerable<Activity> activities, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => KeepCounting(activities);
    public Task<bool> PublishStateSpansAsync(IEnumerable<StateSpan> stateSpans, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(stateSpans);
    public Task<bool> PublishProfileActivationsAsync(IEnumerable<ProfileActivation> activations, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(activations);
    public Task<bool> PublishSystemEventsAsync(IEnumerable<SystemEvent> systemEvents, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(systemEvents);
    public Task<bool> PublishNotesAsync(IEnumerable<Note> records, string source, WriteOrigin origin, CancellationToken cancellationToken = default) => Keep(records);
    public Task<bool> PublishBodyWeightsAsync(IEnumerable<BodyWeight> records, string source, CancellationToken cancellationToken = default) => Keep(records);
    public Task<bool> PublishStepCountsAsync(IEnumerable<StepCount> records, string source, CancellationToken cancellationToken = default) => Keep(records);
    public Task<bool> PublishHeartRatesAsync(IEnumerable<HeartRate> records, string source, CancellationToken cancellationToken = default) => Keep(records);
    public Task<DateTime?> GetLatestActivityTimestampAsync(string source, CancellationToken cancellationToken = default) => Task.FromResult<DateTime?>(null);
    public Task<DateTime?> GetLatestStateSpanTimestampAsync(string source, CancellationToken cancellationToken = default) => Task.FromResult<DateTime?>(null);
}
