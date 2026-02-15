using Nocturne.Core.Models;

namespace Nocturne.Connectors.Core.Interfaces;

public interface IConnectorPublisher
{
    bool IsAvailable { get; }

    Task<bool> PublishEntriesAsync(
        IEnumerable<Entry> entries,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> PublishTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> PublishDeviceStatusAsync(
        IEnumerable<DeviceStatus> deviceStatuses,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> PublishProfilesAsync(
        IEnumerable<Profile> profiles,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> PublishFoodAsync(
        IEnumerable<Food> foods,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> PublishConnectorFoodEntriesAsync(
        IEnumerable<ConnectorFoodEntryImport> entries,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> PublishActivityAsync(
        IEnumerable<Activity> activities,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> PublishStateSpansAsync(
        IEnumerable<StateSpan> stateSpans,
        string source,
        CancellationToken cancellationToken = default);

    Task<bool> PublishSystemEventsAsync(
        IEnumerable<SystemEvent> systemEvents,
        string source,
        CancellationToken cancellationToken = default);

    Task<DateTime?> GetLatestEntryTimestampAsync(
        string source,
        CancellationToken cancellationToken = default);

    Task<DateTime?> GetLatestTreatmentTimestampAsync(
        string source,
        CancellationToken cancellationToken = default);
}