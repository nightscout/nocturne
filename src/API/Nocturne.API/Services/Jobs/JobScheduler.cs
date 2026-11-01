using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Jobs;

/// <summary>
/// Runs every registered <see cref="IScheduledJob"/> from <see cref="IJobLedger"/>: it enrols each
/// job's rows, claims the due ones, runs up to <see cref="Parallelism"/> at once (<c>Jobs:Parallelism</c>,
/// default <see cref="JobSchedulerOptions.DefaultParallelism"/>), and writes each run's outcome back
/// to its row.
/// </summary>
/// <remarks>
/// <para>
/// The ledger holds every schedule and cursor, so a restart resumes where the last process stopped
/// rather than replaying work: a row is due when its time has come, not when the process starts.
/// The claim leases a row for <see cref="LeaseLength"/>, renewed while the run lasts, so replicas
/// sharing the ledger never run one row at once, and a row whose holder died is claimable when its
/// lease runs out. A run whose lease is lost is cancelled and records nothing. Only rows of jobs this
/// process has are claimed, so during a rolling deploy each version runs only the jobs it knows.
/// </para>
/// <para>
/// A run that throws keeps its cursor and is due again after <see cref="Backoff"/>. A run that throws
/// once shutdown has begun, whatever it throws, is not a failure: it releases its lease and keeps its
/// due time and cursor, so the next holder resumes at once.
/// Each run gets a scope of its own for its row's tenant, reading as no one and system-attributed as
/// <c>job:&lt;name&gt;</c>.
/// </para>
/// <para>
/// A claim that fills every free slot, or all the room a group's <see cref="IScheduledJob.MaxConcurrency"/>
/// left it, may have left due rows behind, so until a claim comes back short of both each finished run
/// claims again at once; an idle ledger is read every <see cref="PollInterval"/>.
/// </para>
/// </remarks>
public sealed class JobScheduler : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan EnrolInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan LeaseLength = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(40);
    internal static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(1);

    private const int MaxErrorLength = 2000;

    private readonly Dictionary<string, IScheduledJob> _jobs;
    private readonly IJobLedger _ledger;
    private readonly IServiceProvider _root;
    private readonly TimeProvider _time;
    private readonly JobMetrics _metrics;
    private readonly ILogger<JobScheduler> _logger;
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private readonly ConcurrentDictionary<string, int> _runningByGroup = new(StringComparer.Ordinal);
    private int _running;
    private bool _backlog;
    private TaskCompletionSource _wake = NewWake();

    public JobScheduler(
        IEnumerable<IScheduledJob> jobs,
        IJobLedger ledger,
        IServiceProvider root,
        TimeProvider time,
        JobMetrics metrics,
        IOptions<JobSchedulerOptions> options,
        ILogger<JobScheduler> logger)
    {
        var byName = new Dictionary<string, IScheduledJob>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            if (!byName.TryAdd(job.Name, job))
                throw new InvalidOperationException($"Two scheduled jobs are named '{job.Name}'.");
        }

        foreach (var group in byName.Values.GroupBy(j => j.ConcurrencyGroup, StringComparer.Ordinal))
        {
            if (group.Select(j => j.MaxConcurrency).Distinct().Count() > 1)
                throw new InvalidOperationException($"The jobs of concurrency group '{group.Key}' declare different caps.");
        }

        _jobs = byName;
        _ledger = ledger;
        _root = root;
        _time = time;
        _metrics = metrics;
        _logger = logger;
        Parallelism = Math.Max(1, options.Value.Parallelism);
    }

    /// <summary>How many runs this process holds at once.</summary>
    internal int Parallelism { get; }

    /// <summary>This process's name, which prefixes the token of each lease it takes.</summary>
    internal string Holder { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    /// <summary>Whether a finished run has woken the loop and the loop has not yet started over.</summary>
    internal bool WakePending => Volatile.Read(ref _wake).Task.IsCompleted;

    /// <summary>
    /// How long a job waits after its <paramref name="attempts"/>th failure in a row: one minute,
    /// doubling, up to the lower of <see cref="IScheduledJob.DefaultMaxBackoff"/> and <paramref name="cap"/>.
    /// </summary>
    internal static TimeSpan Backoff(int attempts, TimeSpan cap)
    {
        var limit = cap < IScheduledJob.DefaultMaxBackoff ? cap : IScheduledJob.DefaultMaxBackoff;
        var wait = FirstBackoff * Math.Pow(2, Math.Clamp(attempts - 1, 0, 30));
        return wait < limit ? wait : limit;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Detach from the sequential hosted-service startup chain before touching anything.
        await Task.Yield();

        var enrolledAt = DateTimeOffset.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var wake = Volatile.Read(ref _wake);
                try
                {
                    if (_time.GetUtcNow() - enrolledAt >= EnrolInterval)
                    {
                        await EnrolAsync(stoppingToken);
                        enrolledAt = _time.GetUtcNow();
                    }

                    await PollAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "The job scheduler could not read the job ledger");
                }

                using var tick = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                await Task.WhenAny(wake.Task, Task.Delay(PollInterval, _time, tick.Token));
                await tick.CancelAsync();
                Interlocked.CompareExchange(ref _wake, NewWake(), wake);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await DrainAsync();
        }
    }

    /// <summary>Gives every job its rows and refreshes the failing count.</summary>
    internal async Task EnrolAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        foreach (var job in _jobs.Values)
        {
            switch (job.Enrolment)
            {
                case JobEnrolment.PerTenant:
                    await _ledger.EnrolPerTenantAsync(job.Name, now, job.Spread, ct);
                    break;
                case JobEnrolment.Platform:
                    await _ledger.EnrolPlatformAsync(job.Name, now, ct);
                    break;
            }
        }

        _metrics.SetFailing(await _ledger.CountFailingAsync(ct));
    }

    /// <summary>
    /// Claims as many due rows as there are free slots, and no more of one concurrency group's rows
    /// than its <see cref="IScheduledJob.MaxConcurrency"/> leaves room for, and starts a run for each.
    /// </summary>
    /// <returns>How many runs it started.</returns>
    internal async Task<int> PollAsync(CancellationToken ct)
    {
        var free = Parallelism - Volatile.Read(ref _running);
        if (free <= 0)
            return 0;

        var room = new Dictionary<string, int>(StringComparer.Ordinal);
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var job in _jobs.Values)
        {
            var left = job.MaxConcurrency - _runningByGroup.GetValueOrDefault(job.ConcurrencyGroup);
            if (left <= 0)
                continue;
            room[job.Name] = left;
            groups[job.Name] = job.ConcurrencyGroup;
        }

        if (room.Count == 0)
            return 0;

        var now = _time.GetUtcNow();
        var leases = await _ledger.ClaimDueAsync(Holder, room, groups, free, now, now + LeaseLength, ct);
        Volatile.Write(
            ref _backlog,
            leases.Count == free
            || leases.GroupBy(l => groups[l.Job]).Any(claimed => claimed.Count() == room[claimed.First().Job]));
        foreach (var lease in leases)
        {
            Interlocked.Increment(ref _running);
            _runningByGroup.AddOrUpdate(_jobs[lease.Job].ConcurrencyGroup, 1, (_, n) => n + 1);
            var run = Task.Run(() => RunAsync(lease, now, ct), CancellationToken.None);
            _inFlight.TryAdd(run, 0);
            _ = run.ContinueWith(t => _inFlight.TryRemove(t, out _), TaskScheduler.Default);
        }

        return leases.Count;
    }

    /// <summary>Waits for every run started so far.</summary>
    internal Task DrainAsync() => Task.WhenAll(_inFlight.Keys);

    private async Task RunAsync(JobLease lease, DateTimeOffset startedAt, CancellationToken ct)
    {
        var job = _jobs[lease.Job];
        var started = _time.GetTimestamp();
        var outcome = "failed";
        try
        {
            using var leaseLost = new CancellationTokenSource();
            using var running = CancellationTokenSource.CreateLinkedTokenSource(ct, leaseLost.Token);
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var renewing = RenewLeaseAsync(lease, leaseLost, heartbeat.Token);

            JobOutcome? result = null;
            Exception? thrown = null;
            try
            {
                await using var scope = TenantJobScope.Open(_root, lease.Tenant, $"job:{job.Name}");
                result = await job.RunAsync(
                    new JobRun(lease.Tenant, lease.Cursor, lease.Attempts, scope.ServiceProvider, startedAt),
                    running.Token);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            finally
            {
                // Stopped before the ledger write, so no beat lands on a row the write released.
                await heartbeat.CancelAsync();
                await renewing;
            }

            if (leaseLost.IsCancellationRequested)
            {
                if (thrown is not null)
                {
                    _logger.Log(
                        thrown is OperationCanceledException ? LogLevel.Debug : LogLevel.Warning,
                        thrown,
                        "Scheduled job {Job} ended on row {RowId} after losing its lease",
                        lease.Job, lease.Id);
                }

                outcome = "lost";
            }
            else if (thrown is not null && ct.IsCancellationRequested)
            {
                await _ledger.ReleaseAsync(lease, CancellationToken.None);
                outcome = "cancelled";
            }
            else if (thrown is not null)
            {
                _logger.LogWarning(thrown, "Scheduled job {Job} failed for tenant {TenantId}", lease.Job, lease.Tenant?.TenantId);
                var error = $"{thrown.GetType().Name}: {thrown.Message}";
                await _ledger.FailAsync(
                    lease,
                    error.Length > MaxErrorLength ? error[..MaxErrorLength] : error,
                    _time.GetUtcNow() + Backoff(lease.Attempts + 1, job.MaxBackoff),
                    CancellationToken.None);
            }
            else
            {
                var (cursor, nextDueAt) = result is JobOutcome.Resumed resumed
                    ? (resumed.Cursor, resumed.NextDueAt)
                    : ((JobCursor?)null, (DateTimeOffset?)null);
                await _ledger.CompleteAsync(lease, _time.GetUtcNow(), cursor, nextDueAt, CancellationToken.None);
                outcome = nextDueAt is null ? "done" : "resumed";
            }
        }
        catch (Exception ex)
        {
            // The lease runs out on its own, and the row is claimed again then.
            _logger.LogError(ex, "Could not record the run of scheduled job {Job}", lease.Job);
        }
        finally
        {
            _runningByGroup.AddOrUpdate(job.ConcurrencyGroup, 0, (_, n) => n - 1);
            Interlocked.Decrement(ref _running);
            if (Volatile.Read(ref _backlog))
                Volatile.Read(ref _wake).TrySetResult();
            _metrics.RecordRun(lease.Job, outcome, _time.GetElapsedTime(started));
        }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Extends the lease every <see cref="HeartbeatInterval"/> until <paramref name="ct"/> ends it.
    /// A failed extension is retried at the next beat; only the ledger answering that the lease is no
    /// longer held stops it, and that cancels the run through <paramref name="leaseLost"/>. Never throws.
    /// </summary>
    private async Task RenewLeaseAsync(JobLease lease, CancellationTokenSource leaseLost, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, _time, ct);
                if (await _ledger.ExtendLeaseAsync(lease, _time.GetUtcNow() + LeaseLength, ct))
                    continue;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not renew the lease of scheduled job {Job}; retrying at the next beat", lease.Job);
                continue;
            }
            catch (Exception)
            {
                return;
            }

            _logger.LogWarning(
                "Scheduled job {Job} lost its lease on row {RowId}; the run is cancelled and records nothing",
                lease.Job, lease.Id);
            try
            {
                await leaseLost.CancelAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A cancellation callback of scheduled job {Job} threw", lease.Job);
            }

            return;
        }
    }
}

/// <summary><see cref="JobScheduler"/>'s settings, bound from <c>Jobs</c>.</summary>
public sealed class JobSchedulerOptions
{
    public const string SectionName = "Jobs";

    /// <summary>
    /// A connector run holds its slot for its whole sync, up to its per-tenant timeout, so the
    /// default covers the connectors' concurrency as well as the short jobs.
    /// </summary>
    public const int DefaultParallelism = 12;

    /// <summary>How many runs one process holds at once.</summary>
    public int Parallelism { get; set; } = DefaultParallelism;
}
