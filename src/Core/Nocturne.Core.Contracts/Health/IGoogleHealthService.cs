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
}

public interface IGoogleHealthSyncCoordinator
{
    SemaphoreSlim Gate(Guid tenantId);
    void Report(
        Guid tenantId,
        GoogleHealthSyncPhase phase,
        string? dataType = null,
        int? completedDataTypes = null,
        int? totalDataTypes = null,
        int? pagesRead = null);
}

public interface IGoogleHealthReadingWriter
{
    Task<Guid> BeginReconciliationAsync(
        IReadOnlyCollection<string> activeTypes,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);
    Task StageReconciliationIdsAsync(
        Guid runId,
        string dataType,
        IReadOnlyCollection<string> identifiers,
        CancellationToken ct);
    Task CompleteReconciliationAsync(Guid runId, CancellationToken ct);
    Task AbandonReconciliationAsync(Guid runId, CancellationToken ct);
    Task WriteAsync(
        IReadOnlyCollection<GoogleHealthReading> readings,
        IReadOnlyCollection<SleepSession> sleepSessions,
        int batchSize,
        CancellationToken ct);
    Task ReconcileAsync(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> readingIds,
        IReadOnlyCollection<string> sleepIds,
        IReadOnlyCollection<string> activeTypes,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);
    Task PurgeAsync(CancellationToken ct);
}
