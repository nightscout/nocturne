using Nocturne.Core.Models.Health;
using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Health;

public interface IGoogleHealthService
{
    Task<GoogleHealthStatus> StatusAsync(CancellationToken ct);
    Task SaveAsync(GoogleHealthOptions options, Guid subject, CancellationToken ct);
    Task<GoogleHealthAuthorize> StartAsync(Guid subject, CancellationToken ct);
    Task CompleteAsync(GoogleHealthCallback callback, Guid subject, CancellationToken ct);
    Task DisconnectAsync(Guid subject, CancellationToken ct);
    Task PurgeAsync(Guid subject, CancellationToken ct);
    Task<GoogleHealthPreview> PreviewAsync(Guid subject, CancellationToken ct);
    Task QueueSyncAsync(CancellationToken ct);
    Task SyncAsync(bool force, CancellationToken ct);
}

public interface IGoogleHealthReadingWriter
{
    Task WriteAsync(
        IReadOnlyCollection<GoogleHealthReading> readings,
        IReadOnlyCollection<SleepSession> sleepSessions,
        IReadOnlyCollection<string> activeTypes,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);
    Task PurgeAsync(CancellationToken ct);
}
