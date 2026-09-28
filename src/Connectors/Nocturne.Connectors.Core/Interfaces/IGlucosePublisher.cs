using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Connectors.Core.Interfaces;

public interface IGlucosePublisher
{
    Task<bool> PublishEntriesAsync(
        IEnumerable<Entry> entries,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes the entries a source may already have delivered that nothing stored holds yet under
    /// their id. A held entry is left as it is stored, and one the user deleted does not come back.
    /// </summary>
    /// <returns>How many were written, or null when the write failed.</returns>
    Task<int?> PublishRecentEntriesAsync(
        IEnumerable<Entry> entries,
        string source,
        WriteOrigin origin,
        CancellationToken cancellationToken = default);

    Task<bool> PublishSensorGlucoseAsync(
        IEnumerable<SensorGlucose> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default);

    Task<DateTime?> GetLatestEntryTimestampAsync(
        string source,
        CancellationToken cancellationToken = default);

    Task<DateTime?> GetLatestSensorGlucoseTimestampAsync(
        string source,
        CancellationToken cancellationToken = default);
}
