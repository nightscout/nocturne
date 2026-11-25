using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Evaluates, per tenant and off the request, the rules the facts its committed writes moved
/// (<see cref="ContextFactWrites"/>), through <see cref="ICanonicalAlertEvaluator"/>. Writes coalesce:
/// an evaluation takes every fact moved since the tenant's last one began.
/// </summary>
/// <remarks>
/// A moved glucose fact starts an evaluation at once. It coalesces only into a glucose evaluation that
/// has not yet begun, which the two announcements of one insert usually make one; once an evaluation
/// has begun, the next glucose change starts its own, overlapping it, so a reading never waits on
/// another evaluation's delivery. An overlapping pass on the same reading stops after its canonical
/// read (<see cref="AlertEvaluationWatermark.TryClaim"/>), and the rule lock and the evaluation gate
/// serialise what overlapping passes decide, as they do for the sweep. Any other fact starts one
/// <see cref="Debounce"/> after the tenant's last unhandled change, and no later than
/// <see cref="MaxWait"/> after its first, once the tenant's previous debounced evaluation has ended;
/// a batch write is one evaluation, and a change that lands while it runs is taken by the next. A
/// rule save (<see cref="MovedFacts.Rules"/>, announced by <see cref="AlertRuleRearm"/>) is debounced
/// with them, or joins a glucose evaluation that has not yet begun.
/// <para>
/// Each evaluation runs in a scope of its own that reads as no one, pinned to the tenant the write
/// was made for (<see cref="TenantJobScope.Open(IServiceProvider, TenantContext?, string)"/>), with
/// none of the writing request's execution context: it returns nothing to anyone, and a
/// history-clamped writer's write must still evaluate rules over the tenant's full context.
/// </para>
/// <para>
/// The host stops it after the server has drained its requests (<see cref="StoppedAsync"/>): every
/// pending evaluation starts at once and the host waits for all of them, as it waited for the
/// requests that evaluated inline before. Only the host's shutdown timeout drops work.
/// </para>
/// </remarks>
public sealed class AlertFactEvaluationScheduler : IHostedLifecycleService, IDisposable
{
    /// <summary>How long after a tenant's last non-glucose change its evaluation starts.</summary>
    internal static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    /// <summary>The longest a stream of non-glucose changes can hold back a tenant's evaluation.</summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(5);

    internal const string AuditEndpoint = "service:alert-fact-evaluation";

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<AlertFactEvaluationScheduler> _logger;
    private readonly CancellationTokenSource _abandon = new();
    private readonly Dictionary<Guid, TenantQueue> _tenants = [];
    private readonly HashSet<Task> _runs = [];
    private readonly Lock _gate = new();
    private bool _draining;

    public AlertFactEvaluationScheduler(
        IServiceScopeFactory scopes, TimeProvider time, ILogger<AlertFactEvaluationScheduler> logger)
    {
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    private sealed class TenantQueue(TenantContext tenant)
    {
        public TenantContext Tenant { get; set; } = tenant;
        public HashSet<string> Pending { get; } = new(StringComparer.Ordinal);
        public HashSet<Guid> PendingRules { get; } = [];
        public bool PendingGlucoseWithoutReading { get; set; }
        public long FirstAt { get; set; }
        public long LastAt { get; set; }

        /// <summary>A glucose evaluation is launched and has not yet taken the pending facts.</summary>
        public bool GlucoseQueued { get; set; }

        public int GlucoseRunning { get; set; }
        public bool DebouncedRunning { get; set; }
        public ITimer? Timer { get; set; }

        public bool Idle => !GlucoseQueued && GlucoseRunning == 0 && !DebouncedRunning;

        public bool HasPending => Pending.Count > 0 || PendingRules.Count > 0;

        public MovedFacts TakePending()
        {
            var moved = new MovedFacts(Pending.ToHashSet(StringComparer.Ordinal), PendingGlucoseWithoutReading)
            {
                Rules = PendingRules.ToHashSet(),
            };
            Pending.Clear();
            PendingRules.Clear();
            PendingGlucoseWithoutReading = false;
            return moved;
        }
    }

    /// <summary>Records that a committed write for <paramref name="tenant"/> moved <paramref name="moved"/>.</summary>
    public void Moved(TenantContext tenant, MovedFacts moved)
    {
        if (moved.IsEmpty || _abandon.IsCancellationRequested) return;

        lock (_gate)
        {
            if (!_tenants.TryGetValue(tenant.TenantId, out var queue))
                _tenants[tenant.TenantId] = queue = new TenantQueue(tenant);

            var now = _time.GetTimestamp();
            if (!queue.HasPending)
                queue.FirstAt = now;
            queue.LastAt = now;
            queue.Tenant = tenant;
            queue.Pending.UnionWith(moved.Facts);
            queue.PendingRules.UnionWith(moved.Rules);
            queue.PendingGlucoseWithoutReading |= moved.GlucoseWithoutReading;
            StartOrArm(queue);
        }
    }

    /// <summary>Starts the tenant's evaluation when it is due, or arms its timer for when it will be.</summary>
    private void StartOrArm(TenantQueue queue)
    {
        if (!queue.HasPending)
        {
            DisarmIfIdle(queue);
            return;
        }

        if (queue.GlucoseQueued) return;

        if (queue.Pending.Contains(ContextFactWrites.Glucose))
        {
            Launch(queue, glucose: true);
            return;
        }

        if (queue.DebouncedRunning) return;

        var due = _draining
            ? TimeSpan.Zero
            : Min(Debounce - _time.GetElapsedTime(queue.LastAt), MaxWait - _time.GetElapsedTime(queue.FirstAt));
        if (due <= TimeSpan.Zero)
        {
            Launch(queue, glucose: false);
            return;
        }

        queue.Timer?.Dispose();
        queue.Timer = WithoutFlow(() => _time.CreateTimer(OnTimer, queue, due, Timeout.InfiniteTimeSpan));
    }

    private void OnTimer(object? state)
    {
        lock (_gate)
        {
            StartOrArm((TenantQueue)state!);
        }
    }

    /// <summary>
    /// Launches an evaluation. A glucose one takes the pending facts as it begins, so changes that
    /// land before then join it; a debounced one takes them now.
    /// </summary>
    private void Launch(TenantQueue queue, bool glucose)
    {
        queue.Timer?.Dispose();
        queue.Timer = null;
        MovedFacts? moved = null;
        if (glucose)
        {
            queue.GlucoseQueued = true;
        }
        else
        {
            moved = queue.TakePending();
            queue.DebouncedRunning = true;
        }

        var run = WithoutFlow(() => Task.Run(() => RunAsync(queue, glucose, moved)));
        _runs.Add(run);
        WithoutFlow(() => run.ContinueWith(
            finished =>
            {
                if (finished.Exception is { } fault)
                    _logger.LogError(fault, "An evaluation of the alert rules a write moved failed");
                lock (_gate) _runs.Remove(finished);
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
    }

    private async Task RunAsync(TenantQueue queue, bool glucose, MovedFacts? moved)
    {
        var tenant = queue.Tenant;
        if (glucose)
        {
            lock (_gate)
            {
                moved = queue.TakePending();
                queue.GlucoseQueued = false;
                queue.GlucoseRunning++;
                tenant = queue.Tenant;
            }
        }

        try
        {
            if (!moved!.IsEmpty)
            {
                await using var scope = TenantJobScope.Open(_scopes, tenant, AuditEndpoint);
                await scope.ServiceProvider.GetRequiredService<ICanonicalAlertEvaluator>()
                    .EvaluateAsync(moved, ReadingReport.Here, _abandon.Token);
            }
        }
        catch (OperationCanceledException) when (_abandon.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "The evaluation of the alert rules a write moved was cancelled for tenant {TenantId}", tenant.TenantId);
        }
        catch (ObjectDisposedException) when (_abandon.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogError(ex, "Could not open the scope to evaluate the alert rules a write moved for tenant {TenantId}", tenant.TenantId);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Could not evaluate the alert rules a write moved for tenant {TenantId}", tenant.TenantId);
        }
        finally
        {
            lock (_gate)
            {
                if (glucose)
                    queue.GlucoseRunning--;
                else
                    queue.DebouncedRunning = false;
                if (!_abandon.IsCancellationRequested)
                    StartOrArm(queue);
            }
        }
    }

    /// <summary>Forgets an idle tenant, so the map holds only tenants with work.</summary>
    private void DisarmIfIdle(TenantQueue queue)
    {
        if (!queue.Idle) return;
        queue.Timer?.Dispose();
        queue.Timer = null;
        if (_tenants.TryGetValue(queue.Tenant.TenantId, out var current) && ReferenceEquals(current, queue))
            _tenants.Remove(queue.Tenant.TenantId);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static T WithoutFlow<T>(Func<T> start)
    {
        if (ExecutionContext.IsFlowSuppressed())
            return start();
        using (ExecutionContext.SuppressFlow())
            return start();
    }

    /// <summary>
    /// Starts every pending evaluation at once and waits for every evaluation to end, including those
    /// the ending ones start, until <paramref name="cancellationToken"/>, the host's shutdown timeout,
    /// fires. Then it abandons the rest. Runs after the server has drained, so the writes of requests
    /// in flight at shutdown are evaluated too.
    /// </summary>
    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _draining = true;
            foreach (var queue in _tenants.Values.ToList())
                StartOrArm(queue);
        }

        try
        {
            while (true)
            {
                Task[] runs;
                lock (_gate)
                    runs = [.. _runs];
                if (runs.Length == 0)
                    return;
                await Task.WhenAll(runs.Select(Settled)).WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Shutdown timed out with alert evaluations of written facts still running; they are dropped");
            Abandon();
        }
    }

    /// <summary>
    /// Completes when <paramref name="run"/> does, without observing its fault, so one faulted run
    /// does not end the drain; the run's bookkeeping continuation logs the fault.
    /// </summary>
    private static Task Settled(Task run) =>
        run.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Drops pending work and cancels running evaluations. The sweep makes good a dropped evaluation of
    /// a reading (<see cref="AlertSweepJob.LostEvaluationGrace"/>), and of a wall-clock rule.
    /// </summary>
    private void Abandon()
    {
        lock (_gate)
        {
            _abandon.Cancel();
            foreach (var queue in _tenants.Values)
                queue.Timer?.Dispose();
            _tenants.Clear();
        }
    }

    public void Dispose() => Abandon();
}
