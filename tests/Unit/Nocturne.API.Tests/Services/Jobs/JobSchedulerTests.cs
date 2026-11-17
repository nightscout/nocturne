using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Jobs;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Tests.Services.Jobs;

/// <summary>
/// <see cref="JobScheduler"/>'s dispatch over a mocked ledger: what it claims, the scope it runs a job
/// in, and what it writes back. <see cref="JobSchedulerPostgresTests"/> runs it over the real ledger.
/// </summary>
[Trait("Category", "Unit")]
public class JobSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TenantContext Tenant = new(Guid.CreateVersion7(), "t", "T", true, IsDemo: false);

    private readonly LoopClock _clock = new(Now);
    private readonly Mock<IJobLedger> _ledger = new();

    [Fact]
    public void Two_jobs_of_one_name_fail_the_scheduler_at_construction()
    {
        var act = () => Scheduler(new ProbeJob("same"), new ProbeJob("same"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*same*");
    }

    [Fact]
    public async Task It_claims_no_more_rows_than_it_has_free_slots()
    {
        var job = new ProbeJob("probe") { Gate = new TaskCompletionSource() };
        var scheduler = Scheduler(job);
        var claimed = new List<int>();
        _ledger.Setup(l => l.ClaimDueAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, int>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyDictionary<string, int> _, IReadOnlyDictionary<string, string> _, int limit, DateTimeOffset _, DateTimeOffset _, CancellationToken _) =>
            {
                claimed.Add(limit);
                return Enumerable.Range(0, limit).Select(_ => Lease("probe")).ToList();
            });

        await scheduler.PollAsync(default);
        await scheduler.PollAsync(default);
        job.Gate.SetResult();
        await scheduler.DrainAsync();
        await scheduler.PollAsync(default);
        await scheduler.DrainAsync();

        claimed.Should().Equal(JobSchedulerOptions.DefaultParallelism, JobSchedulerOptions.DefaultParallelism);
        job.Runs.Should().HaveCount(2 * JobSchedulerOptions.DefaultParallelism);
    }

    [Fact]
    public async Task A_backlog_runs_to_the_end_within_one_tick_and_an_idle_ledger_is_read_once_a_tick()
    {
        var job = new ProbeJob("probe");
        var scheduler = Scheduler(job);
        var due = 12;
        var completed = 0;
        _ledger.Setup(l => l.ClaimDueAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, int>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyDictionary<string, int> _, IReadOnlyDictionary<string, string> _, int limit, DateTimeOffset now, DateTimeOffset _, CancellationToken _) =>
            {
                var take = Math.Min(limit, due);
                due -= take;
                _clock.Record(new LoopEvent(LoopStep.Claim, now, Short: take < limit));
                return Enumerable.Range(0, take).Select(_ => Lease("probe")).ToList();
            });
        _ledger.Setup(l => l.CompleteAsync(It.IsAny<JobLease>(), It.IsAny<DateTimeOffset>(), It.IsAny<JobCursor?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref completed))
            .Returns(Task.CompletedTask);

        await scheduler.StartAsync(default);
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref completed) == 12);
            await scheduler.DrainAsync();
            await WaitUntilAsync(() => Parked(scheduler));
            var settled = _clock.Events.Count;
            _clock.Events.Where(e => e.Step == LoopStep.Claim).Should()
                .OnlyContain(e => e.At == Now, "the backlog ran without waiting for a tick");

            _clock.Advance(JobScheduler.PollInterval - TimeSpan.FromSeconds(1));
            _clock.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => _clock.Events.Count > settled && Parked(scheduler));

            _clock.Events.Skip(settled).Should().Equal(
                [
                    new LoopEvent(LoopStep.Claim, Now + JobScheduler.PollInterval, Short: true),
                    new LoopEvent(LoopStep.Wait, Now + JobScheduler.PollInterval),
                ],
                "a short claim ends the refill, and an idle ledger is read once a tick");
        }
        finally
        {
            await scheduler.StopAsync(default);
        }

        job.Runs.Should().HaveCount(12);
    }

    /// <summary>
    /// A cancellation the ledger raises without shutdown (a command timeout) ends that poll only:
    /// the loop waits its tick and reads the ledger again.
    /// </summary>
    [Fact]
    public async Task A_ledger_read_cancelled_without_shutdown_does_not_stop_later_polls()
    {
        var scheduler = Scheduler(new ProbeJob("probe"));
        var claims = 0;
        _ledger.Setup(l => l.ClaimDueAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, int>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IReadOnlyDictionary<string, int> _, IReadOnlyDictionary<string, string> _, int limit, DateTimeOffset now, DateTimeOffset _, CancellationToken _) =>
            {
                _clock.Record(new LoopEvent(LoopStep.Claim, now, Short: true));
                return Interlocked.Increment(ref claims) == 1
                    ? Task.FromException<IReadOnlyList<JobLease>>(new OperationCanceledException("timed out"))
                    : Task.FromResult<IReadOnlyList<JobLease>>([]);
            });

        await scheduler.StartAsync(default);
        try
        {
            await WaitUntilAsync(() => Parked(scheduler));
            _clock.Advance(JobScheduler.PollInterval);
            await WaitUntilAsync(() => Volatile.Read(ref claims) == 2 && Parked(scheduler));
        }
        finally
        {
            await scheduler.StopAsync(default);
        }

        _clock.Events.Where(e => e.Step == LoopStep.Claim).Select(e => e.At)
            .Should().Equal(Now, Now + JobScheduler.PollInterval);
    }

    /// <summary>
    /// The loop has claimed, found no backlog and is waiting out its tick: no run can wake it and its
    /// tick's timer is on the clock, so advancing the clock is what moves it next.
    /// </summary>
    private bool Parked(JobScheduler scheduler)
    {
        var events = _clock.Events;
        return events.Count >= 2
               && events[^1].Step == LoopStep.Wait
               && events[^2] is { Step: LoopStep.Claim, Short: true }
               && !scheduler.WakePending;
    }

    /// <summary>
    /// A job at its <see cref="IScheduledJob.MaxConcurrency"/> is left out of the claim, so the claim
    /// never takes a row it cannot run, and is offered again once one of its runs ends.
    /// </summary>
    [Fact]
    public async Task A_job_at_its_cap_is_left_out_of_the_claim_until_a_run_ends()
    {
        var capped = new ProbeJob("capped") { Gate = new TaskCompletionSource(), MaxConcurrency = 2 };
        var other = new ProbeJob("other");
        var scheduler = Scheduler(capped, other);
        var offered = new List<IReadOnlyDictionary<string, int>>();
        var leases = new Queue<IReadOnlyList<JobLease>>([[Lease("capped"), Lease("capped")], [], []]);
        _ledger.Setup(l => l.ClaimDueAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, int>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyDictionary<string, int> jobs, IReadOnlyDictionary<string, string> _, int _, DateTimeOffset _, DateTimeOffset _, CancellationToken _) =>
            {
                offered.Add(new Dictionary<string, int>(jobs));
                return leases.Count > 0 ? leases.Dequeue() : [];
            });

        await scheduler.PollAsync(default);
        await scheduler.PollAsync(default);
        capped.Gate.SetResult();
        await scheduler.DrainAsync();
        await scheduler.PollAsync(default);

        offered[0].Should().ContainKey("capped").WhoseValue.Should().Be(2);
        offered[1].Should().NotContainKey("capped", "both of its runs are still going").And.ContainKey("other");
        offered[2].Should().ContainKey("capped", "its runs have ended").WhoseValue.Should().Be(2);
    }

    /// <summary>
    /// The jobs of one concurrency group share its cap: each is offered the group's room, the claim is
    /// told they are one group, and while the group is full neither is offered.
    /// </summary>
    [Fact]
    public async Task Jobs_of_one_group_share_its_cap()
    {
        var gate = new TaskCompletionSource();
        var a = new ProbeJob("a") { Gate = gate, MaxConcurrency = 2, Group = "g" };
        var b = new ProbeJob("b") { Gate = gate, MaxConcurrency = 2, Group = "g" };
        var other = new ProbeJob("other");
        var scheduler = Scheduler(a, b, other);
        var offered = new List<(Dictionary<string, int> Room, Dictionary<string, string> Groups)>();
        var leases = new Queue<IReadOnlyList<JobLease>>([[Lease("a"), Lease("b")], []]);
        _ledger.Setup(l => l.ClaimDueAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, int>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyDictionary<string, int> jobs, IReadOnlyDictionary<string, string> groups, int _, DateTimeOffset _, DateTimeOffset _, CancellationToken _) =>
            {
                offered.Add((new Dictionary<string, int>(jobs), new Dictionary<string, string>(groups)));
                return leases.Count > 0 ? leases.Dequeue() : [];
            });

        await scheduler.PollAsync(default);
        await scheduler.PollAsync(default);
        gate.SetResult();
        await scheduler.DrainAsync();

        offered[0].Room.Should().Contain("a", 2).And.Contain("b", 2);
        offered[0].Groups.Should().Contain("a", "g").And.Contain("b", "g").And.Contain("other", "other");
        offered[1].Room.Should().NotContainKey("a", "the group holds both of its runs")
            .And.NotContainKey("b").And.ContainKey("other");
    }

    [Fact]
    public void Jobs_of_one_group_with_different_caps_fail_construction()
    {
        var build = () => Scheduler(
            new ProbeJob("a") { MaxConcurrency = 2, Group = "g" },
            new ProbeJob("b") { MaxConcurrency = 3, Group = "g" });

        build.Should().Throw<InvalidOperationException>().WithMessage("*'g'*");
    }

    /// <summary>
    /// A claim that took all the room a job's cap left it may have left that job's due rows behind,
    /// so a run ending wakes the loop to claim again, as a claim that filled every slot does.
    /// </summary>
    [Fact]
    public async Task A_claim_that_filled_a_jobs_cap_claims_again_when_a_run_ends()
    {
        var capped = new ProbeJob("capped") { MaxConcurrency = 2 };
        var scheduler = Scheduler(capped);
        _ledger.Setup(l => l.ClaimDueAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, int>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Lease("capped"), Lease("capped")]);

        await scheduler.PollAsync(default);
        await scheduler.DrainAsync();

        scheduler.WakePending.Should().BeTrue("the claim took every row the cap allowed, short of the free slots");
    }

    [Fact]
    public async Task Parallelism_comes_from_the_options()
    {
        var scheduler = Scheduler(new JobSchedulerOptions { Parallelism = 7 }, new ProbeJob("probe"));
        _ledger.Setup(l => l.ClaimDueAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, int>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await scheduler.PollAsync(default);

        _ledger.Verify(l => l.ClaimDueAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, int>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), 7,
            It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition did not hold within 30 seconds.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_job_runs_in_a_scope_of_its_tenant_attributed_to_the_system_as_the_job()
    {
        var job = new ProbeJob("probe");
        var scheduler = Scheduler(job);
        var lease = Lease("probe", new JobCursor(Page: 3), attempts: 2);
        Claims(lease);

        await scheduler.PollAsync(default);
        await scheduler.DrainAsync();

        var run = job.Runs.Should().ContainSingle().Subject;
        run.Tenant.Should().Be(Tenant);
        run.Cursor.Should().Be(new JobCursor(Page: 3));
        run.Attempts.Should().Be(2);
        run.StartedAt.Should().Be(Now);
        job.SeenTenant.Should().Be(Tenant);
        job.SeenAudit.Should().Be((true, "job:probe"));
        _ledger.Verify(l => l.CompleteAsync(lease, Now, null, null, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_resumed_outcome_writes_its_cursor_and_due_time()
    {
        var next = new JobCursor(Page: 4);
        var scheduler = Scheduler(new ProbeJob("probe") { Outcome = JobOutcome.Resume(next, Now.AddHours(1)) });
        var lease = Lease("probe");
        Claims(lease);

        await scheduler.PollAsync(default);
        await scheduler.DrainAsync();

        _ledger.Verify(l => l.CompleteAsync(lease, Now, next, Now.AddHours(1), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_failure_backs_off_from_the_rows_attempts_and_keeps_the_error_short()
    {
        var scheduler = Scheduler(new ProbeJob("probe") { Throw = new InvalidOperationException(new string('x', 5000)) });
        var lease = Lease("probe", attempts: 2);
        Claims(lease);

        await scheduler.PollAsync(default);
        await scheduler.DrainAsync();

        _ledger.Verify(l => l.FailAsync(
            lease,
            It.Is<string>(e => e.StartsWith("InvalidOperationException: xxx") && e.Length == 2000),
            Now.AddMinutes(4),
            It.IsAny<CancellationToken>()));
        _ledger.Verify(l => l.CompleteAsync(It.IsAny<JobLease>(), It.IsAny<DateTimeOffset>(), It.IsAny<JobCursor?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_job_that_throws_its_own_cancellation_is_failed_not_released()
    {
        var scheduler = Scheduler(new ProbeJob("probe") { Throw = new OperationCanceledException("gave up") });
        var lease = Lease("probe", attempts: 1);
        Claims(lease);

        await scheduler.PollAsync(default);
        await scheduler.DrainAsync();

        _ledger.Verify(l => l.FailAsync(
            lease, "OperationCanceledException: gave up", Now.AddMinutes(2), It.IsAny<CancellationToken>()));
        _ledger.Verify(l => l.ReleaseAsync(It.IsAny<JobLease>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_job_that_turns_shutdown_into_another_exception_is_released_not_failed()
    {
        var job = new ProbeJob("probe") { Gate = new TaskCompletionSource(), WrapsCancellation = true };
        var scheduler = Scheduler(job);
        var lease = Lease("probe");
        Claims(lease);
        using var shutdown = new CancellationTokenSource();

        await scheduler.PollAsync(shutdown.Token);
        await job.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await shutdown.CancelAsync();
        await scheduler.DrainAsync();

        _ledger.Verify(l => l.ReleaseAsync(lease, It.IsAny<CancellationToken>()));
        _ledger.Verify(l => l.FailAsync(It.IsAny<JobLease>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Shutdown_releases_the_lease_and_records_no_failure()
    {
        var job = new ProbeJob("probe") { Gate = new TaskCompletionSource() };
        var scheduler = Scheduler(job);
        var lease = Lease("probe");
        Claims(lease);
        using var shutdown = new CancellationTokenSource();

        await scheduler.PollAsync(shutdown.Token);
        await shutdown.CancelAsync();
        await scheduler.DrainAsync();

        _ledger.Verify(l => l.ReleaseAsync(lease, It.IsAny<CancellationToken>()));
        _ledger.Verify(l => l.FailAsync(It.IsAny<JobLease>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task A_lease_extension_that_throws_is_retried_at_the_next_beat(Type thrown)
    {
        var job = new ProbeJob("probe") { Gate = new TaskCompletionSource() };
        var scheduler = Scheduler(job);
        var lease = Lease("probe");
        Claims(lease);
        var extensions = 0;
        _ledger.Setup(l => l.ExtendLeaseAsync(lease, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref extensions) == 1
                ? Task.FromException<bool>((Exception)Activator.CreateInstance(thrown)!)
                : Task.FromResult(true));

        await scheduler.PollAsync(default);
        await job.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref extensions) < 3 && DateTime.UtcNow < deadline)
        {
            _clock.Advance(JobScheduler.HeartbeatInterval);
            await Task.Delay(20);
        }

        job.Gate.SetResult();
        await scheduler.DrainAsync();

        extensions.Should().BeGreaterThanOrEqualTo(3, "the beats after a failed extension still extend the lease");
        _ledger.Verify(l => l.CompleteAsync(lease, It.IsAny<DateTimeOffset>(), null, null, It.IsAny<CancellationToken>()));
        _ledger.Invocations.Clear();
        await scheduler.PollAsync(default);
        _ledger.Verify(l => l.ClaimDueAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, int>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), JobSchedulerOptions.DefaultParallelism,
            It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), "the run gave its slot back");
    }

    [Fact]
    public async Task A_lost_lease_cancels_the_run_and_records_nothing()
    {
        var job = new ProbeJob("probe") { Gate = new TaskCompletionSource() };
        var scheduler = Scheduler(job);
        var lease = Lease("probe");
        Claims(lease);
        _ledger.Setup(l => l.ExtendLeaseAsync(lease, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await scheduler.PollAsync(default);
        await job.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _clock.Advance(JobScheduler.HeartbeatInterval);
        await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

        job.Gate.Task.IsCompleted.Should().BeFalse("the run ended because its lease was lost, not because it finished");
        _ledger.Verify(l => l.CompleteAsync(It.IsAny<JobLease>(), It.IsAny<DateTimeOffset>(), It.IsAny<JobCursor?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Never);
        _ledger.Verify(l => l.FailAsync(It.IsAny<JobLease>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _ledger.Verify(l => l.ReleaseAsync(It.IsAny<JobLease>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Enrolment_gives_each_job_the_rows_its_enrolment_names()
    {
        var scheduler = Scheduler(
            new ProbeJob("tenants"),
            new ProbeJob("spread") { Spread = TimeSpan.FromMinutes(1) },
            new ProbeJob("platform") { Enrolment = JobEnrolment.Platform },
            new ProbeJob("requested") { Enrolment = JobEnrolment.OnRequest });

        await scheduler.EnrolAsync(default);

        _ledger.Verify(l => l.EnrolPerTenantAsync("tenants", Now, TimeSpan.Zero, It.IsAny<CancellationToken>()));
        _ledger.Verify(l => l.EnrolPerTenantAsync("spread", Now, TimeSpan.FromMinutes(1), It.IsAny<CancellationToken>()));
        _ledger.Verify(l => l.EnrolPlatformAsync("platform", Now, It.IsAny<CancellationToken>()));
        _ledger.Verify(l => l.EnrolPerTenantAsync("requested", It.IsAny<DateTimeOffset>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
        _ledger.Verify(l => l.EnrolPlatformAsync("requested", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(1, 60, 1)]
    [InlineData(2, 60, 2)]
    [InlineData(3, 60, 4)]
    [InlineData(7, 60, 60)]
    [InlineData(40, 60, 60)]
    [InlineData(3, 3, 3)]
    [InlineData(3, 600, 4)]
    [InlineData(9, 600, 60)]
    public void Backoff_doubles_from_a_minute_up_to_the_lower_cap(int attempts, int capMinutes, int expectedMinutes) =>
        JobScheduler.Backoff(attempts, TimeSpan.FromMinutes(capMinutes)).Should().Be(TimeSpan.FromMinutes(expectedMinutes));

    private void Claims(JobLease lease)
    {
        _ledger.SetupSequence(l => l.ClaimDueAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, int>>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([lease])
            .ReturnsAsync([]);
    }

    private static JobLease Lease(string job, JobCursor? cursor = null, int attempts = 0) =>
        new(Guid.CreateVersion7(), job, Tenant, cursor, attempts, Now, $"test:{Guid.NewGuid():N}");

    private JobScheduler Scheduler(params IScheduledJob[] jobs) => Scheduler(new JobSchedulerOptions(), jobs);

    private JobScheduler Scheduler(JobSchedulerOptions options, params IScheduledJob[] jobs)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        var provider = services.BuildServiceProvider();
        return new JobScheduler(
            jobs,
            _ledger.Object,
            provider,
            _clock,
            new JobMetrics(provider.GetRequiredService<IMeterFactory>()),
            Options.Create(options),
            NullLogger<JobScheduler>.Instance);
    }

    private enum LoopStep
    {
        Claim,
        Wait,
    }

    private sealed record LoopEvent(LoopStep Step, DateTimeOffset At, bool Short = false);

    /// <summary>A fake clock that records each tick the scheduler's loop starts waiting out.</summary>
    private sealed class LoopClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private readonly Lock _gate = new();
        private readonly List<LoopEvent> _events = [];

        public IReadOnlyList<LoopEvent> Events
        {
            get
            {
                lock (_gate)
                    return [.. _events];
            }
        }

        public void Record(LoopEvent loopEvent)
        {
            lock (_gate)
                _events.Add(loopEvent);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == JobScheduler.PollInterval)
                Record(new LoopEvent(LoopStep.Wait, GetUtcNow()));
            return timer;
        }
    }

    private sealed class ProbeJob(string name) : IScheduledJob
    {
        public string Name => name;

        public JobEnrolment Enrolment { get; init; } = JobEnrolment.PerTenant;

        public TimeSpan Spread { get; init; }

        public int MaxConcurrency { get; init; } = int.MaxValue;

        public string? Group { get; init; }

        public string ConcurrencyGroup => Group ?? name;

        public TaskCompletionSource? Gate { get; init; }

        public bool WrapsCancellation { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JobOutcome Outcome { get; init; } = JobOutcome.Done;

        public Exception? Throw { get; init; }

        public System.Collections.Concurrent.ConcurrentQueue<JobRun> Runs { get; } = new();

        public TenantContext? SeenTenant { get; private set; }

        public (bool IsSystem, string? Endpoint) SeenAudit { get; private set; }

        public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        {
            Runs.Enqueue(run);
            SeenTenant = run.Services.GetRequiredService<ITenantAccessor>().Context;
            var audit = run.Services.GetRequiredService<IAuditContext>();
            SeenAudit = (audit.IsSystem, audit.Endpoint);
            Started.TrySetResult();
            if (Gate is not null)
            {
                try
                {
                    await Gate.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (WrapsCancellation)
                {
                    throw new InvalidOperationException("the aborted call failed");
                }
            }

            if (Throw is not null)
                throw Throw;
            return Outcome;
        }
    }
}
