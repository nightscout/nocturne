using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models.Health;
using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Health;

public interface IGoogleHealthService
{
    Task<GoogleHealthStatus> StatusAsync(CancellationToken ct);
    Task SaveAsync(GoogleHealthOptions options, Guid subject, CancellationToken ct);
    /// <param name="tenantSlug">
    /// The tenant subdomain the browser started on, carried through the OAuth state so the apex
    /// callback can return there; null when it started on the apex.
    /// </param>
    Task<GoogleHealthAuthorize> StartAsync(Guid subject, string? tenantSlug, CancellationToken ct);
    Task CompleteAsync(GoogleHealthCallback callback, Guid subject, CancellationToken ct);
    Task DisconnectAsync(Guid subject, CancellationToken ct);
    Task ResumeDisconnectAsync(Guid subject, Guid requestId);
    Task PurgeAsync(Guid subject, CancellationToken ct);
    Task<GoogleHealthStatus> PreviewAsync(Guid subject, CancellationToken ct);
    Task QueueSyncAsync(CancellationToken ct);
}

public interface IGoogleHealthSyncCoordinator
{
    Task<IGoogleHealthReadLease> WatchReadsAsync(Guid tenantId, CancellationToken ct);
    Task<IAsyncDisposable?> AcquireAsync(Guid tenantId, CancellationToken ct, TimeSpan? timeout = null);
    Task ReportAsync(
        Guid tenantId,
        GoogleHealthSyncPhase phase,
        string? dataType = null,
        int? completedDataTypes = null,
        int? totalDataTypes = null,
        int? pagesRead = null);
    Task CompleteScheduledAsync(Guid tenantId);
}

public interface IGoogleHealthReadLease : IAsyncDisposable
{
    CancellationToken Token { get; }
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
    /// <summary>
    /// Erases every Google Health record of the tenant, user tombstones included, for a disconnect: the
    /// account is gone, so nothing is kept to block or allow a re-import. A user-requested data delete
    /// goes through the connector purge and <see cref="DeleteSleepAsync"/> instead.
    /// </summary>
    Task PurgeAsync(CancellationToken ct);
    /// <summary>
    /// Soft-deletes the tenant's Google Health sleep sessions for <paramref name="auditContext"/>, so a
    /// sync cannot re-create them, or with <paramref name="allowReimport"/> so it can. Returns the
    /// number deleted.
    /// </summary>
    Task<int> DeleteSleepAsync(bool allowReimport, IAuditContext auditContext, CancellationToken ct);
    /// <summary>
    /// Counts the tenant's Google Health sleep sessions starting from <paramref name="since"/> on, or
    /// all of them when it is null. Returns null when there are none.
    /// </summary>
    Task<GoogleHealthSleepStats?> DescribeSleepAsync(DateTime? since, DateTime last24Hours, CancellationToken ct);
}

public sealed record GoogleHealthSleepStats(long Count, int CountLast24Hours, DateTime Latest, DateTime Oldest);
