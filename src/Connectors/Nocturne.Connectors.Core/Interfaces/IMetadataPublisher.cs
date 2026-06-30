using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Connectors.Core.Interfaces;

public interface IMetadataPublisher
{
    Task<bool> PublishProfilesAsync(
        IEnumerable<Profile> profiles,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishFoodAsync(
        IEnumerable<Food> foods,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConnectorFoodEntry>?> PublishConnectorFoodEntriesAsync(
        IEnumerable<ConnectorFoodEntryImport> entries,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishActivityAsync(
        IEnumerable<Activity> activities,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishStateSpansAsync(
        IEnumerable<StateSpan> stateSpans,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishSystemEventsAsync(
        IEnumerable<SystemEvent> systemEvents,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<bool> PublishNotesAsync(
        IEnumerable<Note> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the timestamp of the most recent activity record for the current tenant,
    /// used by connectors to resume catch-up from where they left off, or <c>null</c> if none exist.
    /// </summary>
    Task<DateTime?> GetLatestActivityTimestampAsync(
        string source,
        CancellationToken cancellationToken = default);
}
