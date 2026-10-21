namespace Nocturne.API.Services.Health.GoogleHealth;

/// <summary>
/// Where <see cref="GoogleHealthCoordinator"/> keeps the state every API replica must agree on:
/// the cross-replica lock, each tenant's import progress and disconnect request, and the queue
/// that wakes the worker.
/// </summary>
public interface IGoogleHealthCoordinatorStore
{
    /// <summary>
    /// Takes the cross-replica lock for <paramref name="lockClass"/>, or returns null once
    /// <paramref name="timeout"/> passes. The caller already holds the matching in-process gate.
    /// </summary>
    Task<IAsyncDisposable?> LockAsync(Guid tenantId, int lockClass, TimeSpan? timeout, CancellationToken ct);

    Task<GoogleHealthCoordinator.SyncProgress?> ReadProgressAsync(Guid tenantId, CancellationToken ct);
    Task WriteProgressAsync(Guid tenantId, GoogleHealthCoordinator.SyncProgress? progress, CancellationToken ct);
    Task<GoogleHealthCoordinator.DisconnectRequest?> ReadDisconnectAsync(Guid tenantId, CancellationToken ct);
    Task WriteDisconnectAsync(Guid tenantId, GoogleHealthCoordinator.DisconnectRequest? request, CancellationToken ct);
    Task NotifyAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Tenants with queued work: those notified, plus any found pending at startup.</summary>
    IAsyncEnumerable<Guid> ReadRequestsAsync(CancellationToken ct);
}
