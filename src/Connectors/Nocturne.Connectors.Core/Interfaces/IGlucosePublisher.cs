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
