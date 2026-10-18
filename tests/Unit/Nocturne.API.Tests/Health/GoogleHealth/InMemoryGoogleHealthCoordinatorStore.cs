using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nocturne.API.Services.Health.GoogleHealth;

namespace Nocturne.API.Tests.Health.GoogleHealth;

/// <summary>A single-replica coordinator store: the in-process gates are the only lock.</summary>
internal sealed class InMemoryGoogleHealthCoordinatorStore : IGoogleHealthCoordinatorStore
{
    private readonly ConcurrentDictionary<Guid, GoogleHealthCoordinator.SyncProgress> progress = new();
    private readonly ConcurrentDictionary<Guid, GoogleHealthCoordinator.DisconnectRequest> disconnects = new();
    private readonly Channel<Guid> requests = Channel.CreateUnbounded<Guid>();

    public static GoogleHealthCoordinator Coordinator() => new(new InMemoryGoogleHealthCoordinatorStore());

    public Task<IAsyncDisposable?> LockAsync(Guid tenantId, int lockClass, TimeSpan? timeout, CancellationToken ct) =>
        Task.FromResult<IAsyncDisposable?>(new Held());

    public Task<GoogleHealthCoordinator.SyncProgress?> ReadProgressAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult(progress.GetValueOrDefault(tenantId));

    public Task WriteProgressAsync(Guid tenantId, GoogleHealthCoordinator.SyncProgress? value, CancellationToken ct)
    {
        if (value is null) progress.TryRemove(tenantId, out _);
        else progress[tenantId] = value;
        return Task.CompletedTask;
    }

    public Task<GoogleHealthCoordinator.DisconnectRequest?> ReadDisconnectAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult(disconnects.GetValueOrDefault(tenantId));

    public Task WriteDisconnectAsync(Guid tenantId, GoogleHealthCoordinator.DisconnectRequest? request, CancellationToken ct)
    {
        if (request is null) disconnects.TryRemove(tenantId, out _);
        else disconnects[tenantId] = request;
        return Task.CompletedTask;
    }

    public Task NotifyAsync(Guid tenantId, CancellationToken ct)
    {
        requests.Writer.TryWrite(tenantId);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<Guid> ReadRequestsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var tenant in requests.Reader.ReadAllAsync(ct)) yield return tenant;
    }

    private sealed class Held : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
