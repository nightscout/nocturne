using System.Collections.Immutable;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Per-rule mutual exclusion for excursion state transitions.
/// </summary>
/// <remarks>
/// Two evaluation paths drive the same rule's state machine in this process: the 30s
/// <see cref="AlertSweepService"/> and the orchestrator's per-reading pass. Read-modify-write
/// of tracker state is not atomic, so at a crossing instant both can read <c>idle</c>, both
/// open an excursion, and the second state write orphans the first excursion row. Locks are
/// striped per rule id so evaluations of unrelated rules never queue behind each other, and
/// each stripe is dropped once nobody holds or awaits it.
/// </remarks>
public sealed class AlertRuleEvaluationGate
{
    private readonly Dictionary<Guid, Stripe> _stripes = new();
    private readonly AsyncLocal<ImmutableHashSet<Guid>?> _heldByFlow = new();

    /// <summary>
    /// Waits for exclusive access to <paramref name="alertRuleId"/>. Dispose the returned
    /// lease to release it. Inside <see cref="RunExclusiveAsync{T}"/> for the same rule it
    /// returns at once, with a lease that releases nothing.
    /// </summary>
    /// <param name="alertRuleId">The alert rule to serialise on.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IDisposable> AcquireAsync(Guid alertRuleId, CancellationToken ct)
    {
        if (_heldByFlow.Value?.Contains(alertRuleId) == true)
            return NoLease.Instance;

        Stripe stripe;
        lock (_stripes)
        {
            if (!_stripes.TryGetValue(alertRuleId, out stripe!))
            {
                stripe = new Stripe();
                _stripes[alertRuleId] = stripe;
            }

            stripe.Users++;
        }

        try
        {
            await stripe.Semaphore.WaitAsync(ct);
        }
        catch
        {
            Release(alertRuleId, stripe, held: false);
            throw;
        }

        return new Lease(this, alertRuleId, stripe);
    }

    /// <summary>
    /// Runs <paramref name="body"/> holding <paramref name="alertRuleId"/>'s lease, so that
    /// everything it does to the rule, including the leases it takes itself, is one exclusive
    /// section. Work <paramref name="body"/> starts concurrently shares the lease.
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(Guid alertRuleId, Func<Task<T>> body, CancellationToken ct)
    {
        using var lease = await AcquireAsync(alertRuleId, ct);
        var outer = _heldByFlow.Value;
        _heldByFlow.Value = (outer ?? ImmutableHashSet<Guid>.Empty).Add(alertRuleId);
        try
        {
            return await body();
        }
        finally
        {
            _heldByFlow.Value = outer;
        }
    }

    /// <summary>Number of rules currently holding or awaiting a stripe.</summary>
    internal int StripeCount
    {
        get
        {
            lock (_stripes) return _stripes.Count;
        }
    }

    private void Release(Guid alertRuleId, Stripe stripe, bool held)
    {
        if (held) stripe.Semaphore.Release();

        lock (_stripes)
        {
            if (--stripe.Users > 0) return;
            if (_stripes.TryGetValue(alertRuleId, out var current) && ReferenceEquals(current, stripe))
            {
                _stripes.Remove(alertRuleId);
            }

            stripe.Semaphore.Dispose();
        }
    }

    private sealed class Stripe
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>Holders plus waiters, mutated only under the dictionary lock.</summary>
        public int Users { get; set; }
    }

    private sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class Lease(AlertRuleEvaluationGate gate, Guid alertRuleId, Stripe stripe) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            gate.Release(alertRuleId, stripe, held: true);
        }
    }
}
