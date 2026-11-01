using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Jobs;
using Nocturne.API.Services.Notifications;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Services.Jobs;

/// <summary>
/// The 30-second sweeps and the dedup reconcile, run from the job ledger over a migrated PostgreSQL
/// under the runtime role and Row Level Security. A "restart" is a fresh scheduler over the same
/// database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SweepJobsPostgresTests(SweepJobsPostgresTests.Database database, ITestOutputHelper output)
    : IClassFixture<SweepJobsPostgresTests.Database>, IAsyncLifetime
{
    public sealed class Database : IAsyncLifetime
    {
        public TestDatabase Created { get; private set; } = null!;

        public async Task InitializeAsync() =>
            Created = await SharedPostgres.CreateMigratedDatabaseAsync("sweep_jobs");

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private const int Tenants = 50;

    /// <summary>The merge lock class <c>DeduplicationService</c> takes per tenant.</summary>
    private const int MergeLockClass = 0x4E44_5550;

    private readonly List<ServiceProvider> _providers = [];
    private readonly string _app = database.Created.AppConnectionString;
    private readonly string _migrator = database.Created.MigratorConnectionString;

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var reset = new NpgsqlCommand("TRUNCATE job_ledger; TRUNCATE tenants CASCADE;", conn);
        await reset.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var provider in _providers)
            await provider.DisposeAsync();
    }

    /// <summary>
    /// Fifty tenants' alert-sweep and notification-resolution rows, all due, are run by one scheduler
    /// at the default parallelism, each once, well inside one sweep interval; a fresh scheduler ten
    /// seconds later runs none of them, and every row is due again one interval after its run.
    /// </summary>
    [Fact]
    public async Task Fifty_tenants_sweeps_all_run_within_one_interval_and_a_restart_does_not_replay_them()
    {
        var enrolledAt = new DateTimeOffset(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var clock = new FakeTimeProvider(enrolledAt);
        for (var i = 0; i < Tenants; i++)
            await SeedTenantAsync();

        var sweep = new TimedJob(new AlertSweepJob(NullLogger<AlertSweepJob>.Instance));
        var resolution = new TimedJob(new NotificationResolutionJob(NullLogger<NotificationResolutionJob>.Instance));
        var first = Scheduler([sweep, resolution], clock);
        await first.EnrolAsync(default);

        // Past every row's phase within the spread, so all of them are due at once.
        clock.Advance(AlertSweepJob.Interval);
        var runAt = clock.GetUtcNow();
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CheckAll()
        {
            if (sweep.Runs.Count + resolution.Runs.Count == 2 * Tenants)
                all.TrySetResult();
        }
        sweep.Ran += CheckAll;
        resolution.Ran += CheckAll;

        var elapsed = Stopwatch.StartNew();
        await first.StartAsync(default);
        try
        {
            await all.Task.WaitAsync(AlertSweepJob.Interval);
        }
        finally
        {
            elapsed.Stop();
            await first.StopAsync(default);
        }

        output.WriteLine(
            $"{2 * Tenants} runs at parallelism {JobSchedulerOptions.DefaultParallelism} in {elapsed.Elapsed.TotalMilliseconds:F0} ms; " +
            $"alert-sweep mean {sweep.MeanMilliseconds:F1} ms, notification-resolution mean {resolution.MeanMilliseconds:F1} ms");
        elapsed.Elapsed.Should().BeLessThan(AlertSweepJob.Interval);
        sweep.Runs.Select(r => r.Tenant).Should().OnlyHaveUniqueItems().And.HaveCount(Tenants);
        resolution.Runs.Select(r => r.Tenant).Should().OnlyHaveUniqueItems().And.HaveCount(Tenants);
        sweep.Runs.Should().OnlyContain(r => r.StartedAt == runAt);

        var rows = (await Ledger().ListAsync(1000, default)).Rows;
        rows.Should().HaveCount(2 * Tenants).And.OnlyContain(r => r.Attempts == 0 && r.LastError == null);
        rows.Should().OnlyContain(r => r.NextDueAt == runAt + AlertSweepJob.Interval);

        clock.Advance(TimeSpan.FromSeconds(10));
        var restartedSweep = new TimedJob(new AlertSweepJob(NullLogger<AlertSweepJob>.Instance));
        var restarted = Scheduler([restartedSweep], clock);
        await restarted.EnrolAsync(default);
        (await RunDueAsync(restarted)).Should().Be(0, "every row is due an interval after its last run, not at start");

        clock.Advance(TimeSpan.FromSeconds(20));
        (await RunDueAsync(restarted)).Should().Be(JobSchedulerOptions.DefaultParallelism, "the rows are due again an interval after their run");
    }

    /// <summary>
    /// A run that finds the tenant's merge lock held leaves its merges, and the reconcile cursor, to
    /// the next run without failing; once the lock is free a run reconciles the link and moves the
    /// cursor.
    /// </summary>
    [Fact]
    public async Task Dedup_reconciliation_with_the_merge_lock_held_leaves_the_merges_to_the_next_interval_without_failing()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero));
        var tenant = await SeedTenantAsync();
        await ExecuteAsync(tenant, $"""
            INSERT INTO linked_records (id, tenant_id, canonical_id, record_type, record_id, source_timestamp, data_source, is_primary, sys_created_at)
            VALUES ('{Guid.CreateVersion7()}', '{tenant}', '{Guid.CreateVersion7()}', 'not-a-record-type', '{Guid.CreateVersion7()}', 0, 'test', true, now() - interval '10 minutes');
            """);
        var scheduler = Scheduler([new DeduplicationReconciliationJob(NullLogger<DeduplicationReconciliationJob>.Instance)], clock);
        await scheduler.EnrolAsync(default);
        clock.Advance(DeduplicationReconciliationJob.Interval);
        var heldAt = clock.GetUtcNow();

        await using (var holder = new NpgsqlConnection(_migrator))
        {
            await holder.OpenAsync();
            await using var lockTx = await holder.BeginTransactionAsync();
            await using (var take = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({MergeLockClass}, {LockKey(tenant)});", holder, lockTx))
                await take.ExecuteNonQueryAsync();

            (await RunDueAsync(scheduler)).Should().Be(1);
            await lockTx.RollbackAsync();
        }

        var row = (await Ledger().ListAsync(1000, default)).Rows.Should().ContainSingle().Subject;
        row.Attempts.Should().Be(0);
        row.LastError.Should().BeNull();
        row.LeaseHolder.Should().BeNull();
        row.NextDueAt.Should().Be(heldAt + DeduplicationReconciliationJob.Interval);
        (await ReconcileCursorExistsAsync(tenant)).Should().BeFalse("the held lock left the link to the next run");

        clock.Advance(DeduplicationReconciliationJob.Interval);
        (await RunDueAsync(scheduler)).Should().Be(1);
        (await ReconcileCursorExistsAsync(tenant)).Should().BeTrue();
    }

    /// <summary>
    /// Two connector-group jobs whose runs never end hold the group's eight slots between them, and
    /// no more, so the six tenants' sweeps still run in the four slots left.
    /// </summary>
    [Fact]
    public async Task Stuck_connector_jobs_hold_their_groups_cap_and_the_sweeps_still_run()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero));
        for (var i = 0; i < 6; i++)
            await SeedTenantAsync();
        var gate = new TaskCompletionSource();
        var first = new GatedConnectorJob("connector:first", gate);
        var second = new GatedConnectorJob("connector:second", gate);
        var sweep = new TimedJob(new AlertSweepJob(NullLogger<AlertSweepJob>.Instance));
        var scheduler = Scheduler([first, second, sweep], clock);
        await scheduler.EnrolAsync(default);
        clock.Advance(AlertSweepJob.Interval);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (sweep.Runs.Count < 6)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the sweeps must not wait for the stuck connectors");
            await scheduler.PollAsync(default);
            await Task.Delay(20);
        }

        (first.Started + second.Started).Should().Be(8, "every connector together holds the group's cap");
        gate.SetResult();
        await scheduler.DrainAsync();
    }

    [Fact]
    public async Task The_admin_listing_puts_the_rows_whose_last_run_failed_first_and_says_when_it_is_capped()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        Scheduler([], clock);
        var ledger = Ledger();
        await ledger.EnrolPlatformAsync("a-healthy", clock.GetUtcNow(), default);
        await ledger.EnrolPlatformAsync("b-failing", clock.GetUtcNow(), default);
        var now = clock.GetUtcNow();
        var lease = (await ledger.ClaimDueAsync("test", new Dictionary<string, int> { ["b-failing"] = 1 }, new Dictionary<string, string>(), 1, now, now.AddMinutes(1), default)).Single();
        await ledger.FailAsync(lease, "boom", now.AddMinutes(1), default);

        var listing = await ledger.ListAsync(10, default);
        listing.Rows.Select(r => r.Job).Should().Equal("b-failing", "a-healthy");
        listing.Capped.Should().BeFalse();

        var capped = await ledger.ListAsync(1, default);
        capped.Rows.Select(r => r.Job).Should().Equal(["b-failing"], "a failing row is within the first page");
        capped.Capped.Should().BeTrue();
    }

    private static async Task<int> RunDueAsync(JobScheduler scheduler)
    {
        var started = await scheduler.PollAsync(default);
        await scheduler.DrainAsync();
        return started;
    }

    /// <summary>The ledger of the scheduler built last.</summary>
    private IJobLedger Ledger() => _providers[^1].GetRequiredService<IJobLedger>();

    private JobScheduler Scheduler(IScheduledJob[] jobs, FakeTimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddLogging();
        services.AddDbContextFactory<NocturneDbContext>(o => o
            .UseNpgsql(_app)
            .AddInterceptors(new TenantConnectionInterceptor()));
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddDataServices();
        services.AddSingleton<TimeProvider>(clock);
        services.AddTransient<IJobLedger, JobLedgerRepository>();
        services.AddScoped<IAlertRepository, AlertRepository>();
        services.AddScoped<IInAppNotificationRepository, InAppNotificationRepository>();
        services.AddScoped(_ => Mock.Of<ISignalRBroadcastService>());
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return new JobScheduler(
            jobs,
            provider.GetRequiredService<IJobLedger>(),
            provider,
            clock,
            new JobMetrics(provider.GetRequiredService<IMeterFactory>()),
            Options.Create(new JobSchedulerOptions()),
            NullLogger<JobScheduler>.Instance);
    }

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.CreateVersion7();
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var insert = new NpgsqlCommand(
            "INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES (@id, @slug, 'T', true, now(), now())", conn);
        insert.Parameters.AddWithValue("id", tenant);
        insert.Parameters.AddWithValue("slug", tenant.ToString("N"));
        await insert.ExecuteNonQueryAsync();
        return tenant;
    }

    private async Task ExecuteAsync(Guid tenant, string sql)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT set_config('app.current_tenant_id', '{tenant}', false); {sql}", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> ReconcileCursorExistsAsync(Guid tenant)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using (var pin = new NpgsqlCommand($"SELECT set_config('app.current_tenant_id', '{tenant}', false);", conn))
            await pin.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*) FROM dedup_reconcile_state WHERE tenant_id = '{tenant}' AND last_reconciled_link_id IS NOT NULL;", conn);
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }

    /// <summary>The key <c>AdvisoryLockExtensions</c> folds a tenant id into.</summary>
    private static int LockKey(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        return BitConverter.ToInt32(bytes[..4]) ^ BitConverter.ToInt32(bytes[4..8])
            ^ BitConverter.ToInt32(bytes[8..12]) ^ BitConverter.ToInt32(bytes[12..]);
    }

    /// <summary>A per-tenant job of the connectors' concurrency group whose runs wait on <paramref name="gate"/>.</summary>
    private sealed class GatedConnectorJob(string name, TaskCompletionSource gate) : IScheduledJob
    {
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public string Name => name;

        public JobEnrolment Enrolment => JobEnrolment.PerTenant;

        public int MaxConcurrency => 8;

        public string ConcurrencyGroup => "connectors";

        public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        {
            Interlocked.Increment(ref _started);
            await gate.Task.WaitAsync(ct);
            return JobOutcome.Resume(null, run.StartedAt + TimeSpan.FromMinutes(5));
        }
    }

    /// <summary>Passes each run to <paramref name="inner"/>, recording its tenant, start and real duration.</summary>
    private sealed class TimedJob(IScheduledJob inner) : IScheduledJob
    {
        private readonly ConcurrentQueue<TimeSpan> _durations = new();

        public ConcurrentQueue<(Guid Tenant, DateTimeOffset StartedAt)> Runs { get; } = new();

        public event Action? Ran;

        public double MeanMilliseconds => _durations.Average(d => d.TotalMilliseconds);

        public string Name => inner.Name;

        public JobEnrolment Enrolment => inner.Enrolment;

        public TimeSpan MaxBackoff => inner.MaxBackoff;

        public TimeSpan Spread => inner.Spread;

        public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        {
            var timer = Stopwatch.StartNew();
            var outcome = await inner.RunAsync(run, ct);
            _durations.Enqueue(timer.Elapsed);
            Runs.Enqueue((run.Tenant!.TenantId, run.StartedAt));
            Ran?.Invoke();
            return outcome;
        }
    }
}
