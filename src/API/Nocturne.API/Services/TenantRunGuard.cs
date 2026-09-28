using System.Collections.Concurrent;

namespace Nocturne.API.Services;

/// <summary>
/// Process-wide mutual exclusion for per-tenant runs, so a manual sync and a scheduled sync of
/// the same connector cannot run together, and a tenant cannot have two migration jobs in flight.
/// </summary>
/// <remarks>
/// A caller that finds the key held is refused rather than queued: the run already in flight
/// ingests the same data, and a queued duplicate would only race it. The API runs as a single
/// process per deployment, so this gate is the whole guarantee; a second replica would need a
/// shared store. The holder recorded with a key carries the id of the run that took it, so a
/// refused migration start can name the job it collided with.
/// </remarks>
public sealed class TenantRunGuard
{
    private readonly ConcurrentDictionary<RunKey, Guid> _held = new();

    /// <summary>
    /// Takes the tenant's <paramref name="run"/> key if it is free. Dispose the returned lease to
    /// release it, including on exception and cancellation; a null result means another caller holds it.
    /// </summary>
    /// <param name="tenantId">The tenant the run belongs to.</param>
    /// <param name="run">The run within that tenant, e.g. a connector id or a migration.</param>
    /// <param name="holder">Identifies the holder (e.g. a migration job id) for <see cref="TryGetHolder"/>.</param>
    public IDisposable? TryAcquire(Guid tenantId, string run, Guid holder = default)
    {
        var key = new RunKey(tenantId, run.ToLowerInvariant());
        return _held.TryAdd(key, holder) ? new Lease(_held, key) : null;
    }

    /// <summary>The holder recorded when the key was taken, if another caller holds it.</summary>
    public bool TryGetHolder(Guid tenantId, string run, out Guid holder) =>
        _held.TryGetValue(new RunKey(tenantId, run.ToLowerInvariant()), out holder);

    private readonly record struct RunKey(Guid TenantId, string Run);

    private sealed class Lease(ConcurrentDictionary<RunKey, Guid> held, RunKey key) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
                return;

            _released = true;
            held.TryRemove(key, out _);
        }
    }
}
