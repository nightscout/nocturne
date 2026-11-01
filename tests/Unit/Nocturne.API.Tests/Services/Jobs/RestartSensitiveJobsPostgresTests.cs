using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Jobs;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.API.Tests.Services.Jobs;

/// <summary>
/// The jobs whose resume point used to live in memory, run from the job ledger over a migrated
/// PostgreSQL under the runtime role and Row Level Security. A "restart" is a fresh scheduler over the
/// same database: what it skips, it skipped from the ledger.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RestartSensitiveJobsPostgresTests(RestartSensitiveJobsPostgresTests.Database database)
    : IClassFixture<RestartSensitiveJobsPostgresTests.Database>, IAsyncLifetime
{
    public sealed class Database : IAsyncLifetime
    {
        public TestDatabase Created { get; private set; } = null!;

        public async Task InitializeAsync() =>
            Created = await SharedPostgres.CreateMigratedDatabaseAsync("restart_sensitive_jobs");

        public Task DisposeAsync() => Task.CompletedTask;
    }

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
    /// Temp <c>a</c> lies beside a snapshot written three days ago, temp <c>b</c> beside one written an
    /// hour ago. The first run reads the week and judges both; <c>a</c> is then made Uncorroborated
    /// again, so a run that read the week again would reopen it.
    /// </summary>
    [Fact]
    public async Task Temp_basal_corroboration_resumes_from_its_watermark_after_a_restart()
    {
        var start = new DateTimeOffset(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        var tenant = await SeedTenantAsync();
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var aStart = start.AddDays(-10);
        var bStart = start.AddDays(-20);
        var watermark = start.AddHours(-1);
        await ExecuteAsync(tenant, $$"""
            INSERT INTO temp_basals (id, tenant_id, start_timestamp, rate, device, app, origin, origin_evidence, sys_updated_at)
            VALUES
              ('{{a}}', '{{tenant}}', '{{aStart:O}}', 1.2, 'Trio', 'Trio', 'Algorithm', 'Uncorroborated', '{{start.AddDays(-4):O}}'),
              ('{{b}}', '{{tenant}}', '{{bStart:O}}', 1.2, 'Trio', 'Trio', 'Algorithm', 'Uncorroborated', '{{start.AddDays(-4):O}}');
            INSERT INTO aps_snapshots (id, tenant_id, timestamp, device, aps_system, enacted, enacted_rate, sys_updated_at)
            VALUES
              ('{{Guid.CreateVersion7()}}', '{{tenant}}', '{{aStart.AddMinutes(2):O}}', 'Trio', 'Trio', true, 1.2, '{{start.AddDays(-3):O}}'),
              ('{{Guid.CreateVersion7()}}', '{{tenant}}', '{{bStart.AddMinutes(2):O}}', 'Trio', 'Trio', true, 1.2, '{{watermark:O}}');
            """);

        var firstCaches = new CacheMocks();
        var first = new RecordingJob(new TempBasalOriginCorroborationJob(NullLogger<TempBasalOriginCorroborationJob>.Instance));
        var scheduler = Scheduler(first, clock, firstCaches.Register);
        await scheduler.EnrolAsync(default);
        (await RunDueAsync(scheduler)).Should().Be(1);

        first.Outcomes.Should().ContainSingle().Which.Cursor.Should().Be(new JobCursor(At: watermark));
        (await EvidenceAsync(tenant, a)).Should().NotBe("Uncorroborated", "the first run reads the week");
        firstCaches.VerifyCleared(Times.Once());
        await ExecuteAsync(tenant, $"UPDATE temp_basals SET origin_evidence = 'Uncorroborated' WHERE id = '{a}';");

        clock.Advance(TimeSpan.FromMinutes(2));
        var restartedCaches = new CacheMocks();
        var restartedRuns = new RecordingJob(new TempBasalOriginCorroborationJob(NullLogger<TempBasalOriginCorroborationJob>.Instance));
        var restarted = Scheduler(restartedRuns, clock, restartedCaches.Register);
        await restarted.EnrolAsync(default);
        (await RunDueAsync(restarted)).Should().Be(0, "the row is due an interval after the last run, not at start");

        await Ledger().RequestAsync(TempBasalOriginCorroborationJob.JobName, tenant, default);
        (await RunDueAsync(restarted)).Should().Be(1);

        restartedRuns.Cursors.Should().Equal(new JobCursor(At: watermark));
        (await EvidenceAsync(tenant, a)).Should().Be("Uncorroborated", "a run with a watermark reads only the snapshots written since");
        restartedRuns.Outcomes.Should().ContainSingle().Which.Cursor.Should().Be(new JobCursor(At: watermark));
        restartedCaches.VerifyCleared(Times.Never(), "a run that judged nothing leaves the caches");
    }

    /// <summary>
    /// A snapshot stamped two minutes before the watermark but committed after the run that set it is
    /// read within <see cref="TempBasalOriginCorroborator.CommitMargin"/>, and the temp beside it is
    /// judged; one stamped a second before the margin is not read, and its temp is left as it was.
    /// </summary>
    [Fact]
    public async Task Temp_basal_corroboration_reads_a_snapshot_committed_late_within_the_margin_and_no_further()
    {
        var start = new DateTimeOffset(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var tenant = await SeedTenantAsync();
        var late = Guid.CreateVersion7();
        var older = Guid.CreateVersion7();
        var lateStart = start.AddDays(-5);
        var olderStart = start.AddDays(-6);
        var watermark = start.AddHours(-1);
        var beforeMargin = watermark - TempBasalOriginCorroborator.CommitMargin - TimeSpan.FromSeconds(1);
        await ExecuteAsync(tenant, $$"""
            INSERT INTO temp_basals (id, tenant_id, start_timestamp, rate, device, app, origin, origin_evidence, sys_updated_at)
            VALUES
              ('{{late}}', '{{tenant}}', '{{lateStart:O}}', 1.2, 'Trio', 'Trio', 'Algorithm', 'Uncorroborated', '{{start.AddDays(-4):O}}'),
              ('{{older}}', '{{tenant}}', '{{olderStart:O}}', 1.2, 'Trio', 'Trio', 'Algorithm', 'Uncorroborated', '{{start.AddDays(-4):O}}');
            INSERT INTO aps_snapshots (id, tenant_id, timestamp, device, aps_system, enacted, enacted_rate, sys_updated_at)
            VALUES
              ('{{Guid.CreateVersion7()}}', '{{tenant}}', '{{lateStart.AddMinutes(2):O}}', 'Trio', 'Trio', true, 1.2, '{{watermark.AddMinutes(-2):O}}'),
              ('{{Guid.CreateVersion7()}}', '{{tenant}}', '{{olderStart.AddMinutes(2):O}}', 'Trio', 'Trio', true, 1.2, '{{beforeMargin:O}}');
            """);

        await RunTempBasalJobAsync(tenant, new JobCursor(At: watermark), start);

        (await EvidenceAsync(tenant, late)).Should().NotBe("Uncorroborated");
        (await EvidenceAsync(tenant, older)).Should().Be("Uncorroborated");
    }

    /// <summary>
    /// A quiet interval reads no snapshot; the cursor stays where it was rather than falling back to
    /// the first run's lookback.
    /// </summary>
    [Fact]
    public async Task Temp_basal_corroboration_keeps_its_cursor_when_it_reads_no_snapshot()
    {
        var start = new DateTimeOffset(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var tenant = await SeedTenantAsync();
        await ExecuteAsync(tenant, $$"""
            INSERT INTO aps_snapshots (id, tenant_id, timestamp, device, aps_system, enacted, enacted_rate, sys_updated_at)
            VALUES ('{{Guid.CreateVersion7()}}', '{{tenant}}', '{{start.AddDays(-2):O}}', 'Trio', 'Trio', true, 1.2, '{{start.AddDays(-2):O}}');
            """);
        var cursor = new JobCursor(At: start.AddHours(-1));

        var outcome = await RunTempBasalJobAsync(tenant, cursor, start);

        outcome.Should().Be(JobOutcome.Resume(cursor, start + TempBasalOriginCorroborationJob.Interval));
    }

    private async Task<JobOutcome> RunTempBasalJobAsync(Guid tenant, JobCursor cursor, DateTimeOffset startedAt)
    {
        var caches = new CacheMocks();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NocturneDbContext>(o => o
            .UseNpgsql(_app)
            .AddInterceptors(new TenantConnectionInterceptor()));
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddDataServices();
        caches.Register(services);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        var context = new TenantContext(tenant, tenant.ToString("N"), "T", true, IsDemo: false);
        await using var scope = TenantJobScope.Open(provider, context, $"job:{TempBasalOriginCorroborationJob.JobName}");
        return await new TempBasalOriginCorroborationJob(NullLogger<TempBasalOriginCorroborationJob>.Instance)
            .RunAsync(new JobRun(context, cursor, 0, scope.ServiceProvider, startedAt), CancellationToken.None);
    }

    /// <summary>The caches a temp-basal run clears after it judges something.</summary>
    private sealed class CacheMocks
    {
        private readonly Mock<ILegacyReadCacheInvalidation> _legacy = new();
        private readonly Mock<ITenantCache> _tenant = new();

        public void Register(IServiceCollection services)
        {
            services.AddScoped(_ => _legacy.Object);
            services.AddScoped(_ => _tenant.Object);
        }

        public void VerifyCleared(Times times, string because = "")
        {
            _legacy.Verify(
                l => l.WrittenAsync(It.Is<IReadOnlyCollection<Type>>(models => models.SequenceEqual(new[] { typeof(TempBasal) }))),
                times,
                because);
            _tenant.Verify(c => c.RemoveFamilyAsync(TenantCacheFamily.Statistics, It.IsAny<CancellationToken>()), times, because);
        }
    }

    /// <summary>
    /// The tenant sleeps 23:00 to 07:00 UTC, so a night is ready at 07:15 the next morning; between
    /// nights the row comes due hourly and scans nothing.
    /// </summary>
    [Fact]
    public async Task Compression_low_detection_scans_a_night_once_across_restarts_and_the_next_when_it_is_ready()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 7, 30, 0, TimeSpan.Zero));
        var tenant = await SeedTenantAsync();
        var checkedNights = new ConcurrentQueue<DateOnly>();

        JobScheduler Start(RecordingJob job) => Scheduler(job, clock, services =>
        {
            var settings = new Mock<IUISettingsService>();
            settings.Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new UISettingsConfiguration
            {
                DataQuality = new DataQualitySettings
                {
                    SleepSchedule = new SleepScheduleSettings { Timezone = "UTC", BedtimeHour = 23, WakeTimeHour = 7 },
                },
            });
            var suggestions = new Mock<ICompressionLowRepository>();
            suggestions
                .Setup(r => r.AnySuggestionExistsForNightAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
                .Callback((DateOnly night, CancellationToken _) => checkedNights.Enqueue(night))
                .ReturnsAsync(true);
            services.AddScoped(_ => settings.Object);
            services.AddScoped(_ => suggestions.Object);
            services.AddScoped(_ => Mock.Of<ITherapySettingsResolver>());
            services.AddScoped(_ => Mock.Of<IEntryService>());
            services.AddScoped<ICompressionLowNightWindowResolver, CompressionLowNightWindowResolver>();
        });

        RecordingJob Job() => new(new CompressionLowDetectionJob(
            new CompressionLowDetectionService(clock, NullLogger<CompressionLowDetectionService>.Instance)));

        var first = Start(Job());
        await first.EnrolAsync(default);
        (await RunDueAsync(first)).Should().Be(1);
        checkedNights.Should().Equal(new DateOnly(2026, 10, 19));

        clock.Advance(TimeSpan.FromMinutes(2));
        var restartedRuns = Job();
        var restarted = Start(restartedRuns);
        await restarted.EnrolAsync(default);
        (await RunDueAsync(restarted)).Should().Be(0, "the row is due an hour after the last run");
        await Ledger().RequestAsync(CompressionLowDetectionJob.JobName, tenant, default);
        (await RunDueAsync(restarted)).Should().Be(1);
        restartedRuns.Cursors.Should().Equal(new JobCursor(At: new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero)));
        checkedNights.Should().Equal([new DateOnly(2026, 10, 19)], "a restart does not scan the night again");

        clock.SetUtcNow(new DateTimeOffset(2026, 10, 21, 7, 14, 0, TimeSpan.Zero));
        var beforeReady = Start(Job());
        (await RunDueAsync(beforeReady)).Should().Be(1, "the row came due an hour after the last run");
        checkedNights.Should().Equal([new DateOnly(2026, 10, 19)], "a run before the next night is ready scans nothing");

        clock.Advance(TimeSpan.FromMinutes(1));
        var afterReady = Start(Job());
        (await RunDueAsync(afterReady)).Should().Be(1);
        checkedNights.Should().Equal(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 20));
    }

    private static async Task<int> RunDueAsync(JobScheduler scheduler)
    {
        var started = await scheduler.PollAsync(default);
        await scheduler.DrainAsync();
        return started;
    }

    /// <summary>The ledger of the scheduler built last.</summary>
    private IJobLedger Ledger() => _providers[^1].GetRequiredService<IJobLedger>();

    private JobScheduler Scheduler(IScheduledJob job, FakeTimeProvider clock, Action<IServiceCollection> configure)
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
        configure(services);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return new JobScheduler(
            [job],
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

    private async Task<string?> EvidenceAsync(Guid tenant, Guid tempBasal)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using (var pin = new NpgsqlCommand($"SELECT set_config('app.current_tenant_id', '{tenant}', false);", conn))
            await pin.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand($"SELECT origin_evidence FROM temp_basals WHERE id = '{tempBasal}';", conn);
        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <summary>Passes each run to <paramref name="inner"/>, recording the cursor it starts from and its outcome.</summary>
    private sealed class RecordingJob(IScheduledJob inner) : IScheduledJob
    {
        public ConcurrentQueue<JobCursor?> Cursors { get; } = new();

        public ConcurrentQueue<JobOutcome.Resumed> Outcomes { get; } = new();

        public string Name => inner.Name;

        public JobEnrolment Enrolment => inner.Enrolment;

        public TimeSpan MaxBackoff => inner.MaxBackoff;

        public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
        {
            Cursors.Enqueue(run.Cursor);
            var outcome = await inner.RunAsync(run, ct);
            if (outcome is JobOutcome.Resumed resumed)
                Outcomes.Enqueue(resumed);
            return outcome;
        }
    }
}
