using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Jobs;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.API.Tests.Services.Jobs;

/// <summary>
/// A connector poller run by <see cref="JobScheduler"/> over a migrated PostgreSQL ledger. A
/// "restart" is a fresh scheduler and a fresh poller over the same database, so whatever they do
/// they read from the ledger.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConnectorPollerPostgresTests(ConnectorPollerPostgresTests.Database database)
    : IClassFixture<ConnectorPollerPostgresTests.Database>, IAsyncLifetime
{
    public sealed class Database : IAsyncLifetime
    {
        public TestDatabase Created { get; private set; } = null!;

        public async Task InitializeAsync() =>
            Created = await SharedPostgres.CreateMigratedDatabaseAsync("connector_poller");

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly List<ServiceProvider> _providers = [];
    private readonly string _app = database.Created.AppConnectionString;
    private readonly string _migrator = database.Created.MigratorConnectionString;

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var reset = new NpgsqlCommand("DELETE FROM job_ledger; DELETE FROM tenants;", conn);
        await reset.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var provider in _providers)
            await provider.DisposeAsync();
    }

    [Fact]
    public async Task Each_tenant_syncs_at_its_own_phase_and_a_restart_keeps_it()
    {
        var (early, late) = TwoTenantsWithApartPhases();
        await SeedTenantAsync(early);
        await SeedTenantAsync(late);
        var phaseEarly = PhaseOf(early);
        var phaseLate = PhaseOf(late);

        var (scheduler, poller) = Start_();
        await scheduler.EnrolAsync(default);
        (await DueAtAsync(early)).Should().Be(Start + phaseEarly, "the row is enrolled at the tenant's phase, worked out in SQL as in C#");
        (await DueAtAsync(late)).Should().Be(Start + phaseLate);

        _clock.SetUtcNow(Start + phaseEarly);
        (await RunDueAsync(scheduler)).Should().Be(1, "only the tenant whose phase has come is due");
        _clock.SetUtcNow(Start + phaseLate);
        (await RunDueAsync(scheduler)).Should().Be(1);
        poller.Syncs.Should().Equal((early, Start + phaseEarly), (late, Start + phaseLate));

        _clock.Advance(TimeSpan.FromSeconds(30));
        var (restarted, restartedPoller) = Start_();
        await restarted.EnrolAsync(default);
        (await RunDueAsync(restarted)).Should().Be(0, "a restart replays no sync that is not yet due");

        var earlyDue = await DueAtAsync(early);
        var lateDue = await DueAtAsync(late);
        earlyDue.Should().Be(Start + Interval + phaseEarly);
        (lateDue - earlyDue).Should().Be(phaseLate - phaseEarly, "the tenants stay as far apart as their phases");

        _clock.SetUtcNow(earlyDue);
        (await RunDueAsync(restarted)).Should().Be(1);
        _clock.SetUtcNow(lateDue);
        (await RunDueAsync(restarted)).Should().Be(1);
        restartedPoller.Syncs.Should().Equal((early, earlyDue), (late, lateDue));
    }

    [Fact]
    public async Task A_failing_sync_backs_off_up_to_the_connector_cap_and_a_request_runs_it_at_once()
    {
        var tenant = Guid.CreateVersion7();
        await SeedTenantAsync(tenant);
        var (scheduler, poller) = Start_(fails: true);
        await scheduler.EnrolAsync(default);
        _clock.SetUtcNow(Start + PhaseOf(tenant));

        var waits = new List<TimeSpan>();
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            (await RunDueAsync(scheduler)).Should().Be(1);
            var row = await RowAsync(tenant);
            row.Attempts.Should().Be(attempt);
            waits.Add(row.NextDueAt!.Value - _clock.GetUtcNow());
            _clock.SetUtcNow(row.NextDueAt.Value);
        }

        waits.Should().Equal(
            new[] { 1, 2, 4, 8, 15, 15 }.Select(m => TimeSpan.FromMinutes(m)),
            "the connector caps its backoff at fifteen minutes");

        (await RunDueAsync(scheduler)).Should().Be(1);
        _clock.Advance(TimeSpan.FromMinutes(1));
        (await RunDueAsync(scheduler)).Should().Be(0, "the row is backing off");

        await Ledger().RequestAsync(poller.Name, tenant, default);
        (await RunDueAsync(scheduler)).Should().Be(1, "a request is not held back by the backoff");
        poller.Attempts.Should().Be(8);
    }

    /// <summary>
    /// A tenant with no usable configuration is parked for a day, and a configuration write reopens
    /// it a minute later, and the run that follows syncs.
    /// </summary>
    [Fact]
    public async Task An_unconfigured_row_parks_for_a_day_and_a_configuration_write_reopens_it_a_minute_later()
    {
        var tenant = Guid.CreateVersion7();
        await SeedTenantAsync(tenant);
        _configured = false;
        var nudge = new ConnectorPollerNudge();
        var (scheduler, poller) = Start_(nudge: nudge);
        await scheduler.EnrolAsync(default);
        _clock.SetUtcNow(Start + PhaseOf(tenant));

        (await RunDueAsync(scheduler)).Should().Be(1);
        var parkedUntil = _clock.GetUtcNow() + TimeSpan.FromDays(1);
        (await DueAtAsync(tenant)).Should().Be(parkedUntil);

        _clock.SetUtcNow(parkedUntil - TimeSpan.FromSeconds(1));
        await scheduler.EnrolAsync(default);
        (await RunDueAsync(scheduler)).Should().Be(0, "the row is not claimed again within the day");

        _clock.SetUtcNow(parkedUntil);
        (await RunDueAsync(scheduler)).Should().Be(1, "the row is looked at again after the day");
        poller.Syncs.Should().BeEmpty();

        _configured = true;
        var written = _clock.GetUtcNow();
        nudge.Invalidate("pollertest", tenant);
        await WaitForAsync(async () => await DueAtAsync(tenant) == written + TimeSpan.FromMinutes(1));
        _clock.SetUtcNow(written + TimeSpan.FromMinutes(1));
        (await RunDueAsync(scheduler)).Should().Be(1, "the configuration write made the parked row due a minute later");
        poller.Syncs.Select(s => s.Tenant).Should().Equal(tenant);
        (await DueAtAsync(tenant)).Should().BeAfter(_clock.GetUtcNow(), "a configured row recurs on its interval");
    }

    /// <summary>
    /// A connect flow writes its configuration several times in a few seconds; the writes collapse
    /// to one sync, no sooner than a minute after the last.
    /// </summary>
    [Fact]
    public async Task A_burst_of_configuration_writes_makes_one_sync_a_minute_after_the_last()
    {
        var tenant = Guid.CreateVersion7();
        await SeedTenantAsync(tenant);
        var nudge = new ConnectorPollerNudge();
        var (scheduler, poller) = Start_(nudge: nudge);
        await scheduler.EnrolAsync(default);
        _clock.SetUtcNow(Start + PhaseOf(tenant));
        (await RunDueAsync(scheduler)).Should().Be(1);
        var synced = poller.Syncs.Count;

        var last = _clock.GetUtcNow();
        for (var write = 0; write < 4; write++)
        {
            last = _clock.GetUtcNow();
            nudge.Invalidate("pollertest", tenant);
            var written = last;
            await WaitForAsync(async () => await DueAtAsync(tenant) == written + TimeSpan.FromMinutes(1));
            (await RunDueAsync(scheduler)).Should().Be(0, "a sync started mid-flow would compete with it");
            _clock.Advance(TimeSpan.FromSeconds(3));
        }

        _clock.SetUtcNow(last + TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(1));
        (await RunDueAsync(scheduler)).Should().Be(0);
        _clock.SetUtcNow(last + TimeSpan.FromMinutes(1));
        (await RunDueAsync(scheduler)).Should().Be(1);
        (await RunDueAsync(scheduler)).Should().Be(0, "the burst made one sync");
        poller.Syncs.Should().HaveCount(synced + 1);
    }

    /// <summary>
    /// A new tenant's row is due within a minute of its enrolment. A connect flow that writes its
    /// configuration in that minute defers the row past its enrolment time, to a minute after the last
    /// write; a listener's event then still runs it at once.
    /// </summary>
    [Fact]
    public async Task Configuration_writes_defer_a_row_enrolment_made_due_and_a_listener_event_overrides_them()
    {
        var tenant = TenantWithPhaseAtLeast(TimeSpan.FromSeconds(10));
        await SeedTenantAsync(tenant);
        var nudge = new ConnectorPollerNudge();
        var (scheduler, poller) = Start_(nudge: nudge);
        await scheduler.EnrolAsync(default);
        var enrolledDue = await DueAtAsync(tenant);
        var write = enrolledDue - TimeSpan.FromSeconds(10);
        _clock.SetUtcNow(write);

        foreach (var offset in new[] { 0, 3, 6 })
        {
            _clock.SetUtcNow(write + TimeSpan.FromSeconds(offset));
            nudge.Invalidate("pollertest", tenant);
            var expected = write + TimeSpan.FromSeconds(offset) + TimeSpan.FromMinutes(1);
            await WaitForAsync(async () => await DueAtAsync(tenant) == expected);
        }

        _clock.SetUtcNow(enrolledDue);
        (await RunDueAsync(scheduler)).Should().Be(0, "the writes deferred the row past its enrolment time");
        _clock.SetUtcNow(write + TimeSpan.FromSeconds(20));
        poller.ListenerEvent(tenant);
        await WaitForAsync(async () => await DueAtAsync(tenant) == write + TimeSpan.FromSeconds(20));
        (await RunDueAsync(scheduler)).Should().Be(1, "a listener's event overrides the settle");
        poller.Syncs.Should().HaveCount(1);
    }

    /// <summary>The deferral alone: three writes, no run before a minute after the last, then one.</summary>
    [Fact]
    public async Task Configuration_writes_over_an_enrolled_row_make_one_run_a_minute_after_the_last()
    {
        var tenant = TenantWithPhaseAtLeast(TimeSpan.FromSeconds(10));
        await SeedTenantAsync(tenant);
        var nudge = new ConnectorPollerNudge();
        var (scheduler, poller) = Start_(nudge: nudge);
        await scheduler.EnrolAsync(default);
        var write = await DueAtAsync(tenant) - TimeSpan.FromSeconds(10);

        foreach (var offset in new[] { 0, 3, 6 })
        {
            _clock.SetUtcNow(write + TimeSpan.FromSeconds(offset));
            nudge.Invalidate("pollertest", tenant);
            var expected = write + TimeSpan.FromSeconds(offset) + TimeSpan.FromMinutes(1);
            await WaitForAsync(async () => await DueAtAsync(tenant) == expected);
            (await RunDueAsync(scheduler)).Should().Be(0);
        }

        _clock.SetUtcNow(write + TimeSpan.FromSeconds(66) - TimeSpan.FromMilliseconds(1));
        (await RunDueAsync(scheduler)).Should().Be(0, "no run before a minute after the last write");
        _clock.SetUtcNow(write + TimeSpan.FromSeconds(66));
        (await RunDueAsync(scheduler)).Should().Be(1);
        (await RunDueAsync(scheduler)).Should().Be(0);
        poller.Syncs.Should().HaveCount(1);
    }

    /// <summary>A real-time listener's event on a row a configuration write delayed still runs at once.</summary>
    [Fact]
    public async Task A_listener_event_runs_the_tenant_at_once_despite_a_settling_configuration_write()
    {
        var tenant = Guid.CreateVersion7();
        await SeedTenantAsync(tenant);
        var nudge = new ConnectorPollerNudge();
        var (scheduler, poller) = Start_(nudge: nudge);
        await scheduler.EnrolAsync(default);
        _clock.SetUtcNow(Start + PhaseOf(tenant));
        (await RunDueAsync(scheduler)).Should().Be(1);

        nudge.Invalidate("pollertest", tenant);
        await WaitForAsync(async () => await DueAtAsync(tenant) == _clock.GetUtcNow() + TimeSpan.FromMinutes(1));
        poller.ListenerEvent(tenant);
        await WaitForAsync(async () => await DueAtAsync(tenant) <= _clock.GetUtcNow());

        (await RunDueAsync(scheduler)).Should().Be(1);
    }

    [Fact]
    public async Task An_inactive_tenants_row_parks_for_an_hour()
    {
        var tenant = Guid.CreateVersion7();
        await SeedTenantAsync(tenant, active: false);
        var (scheduler, poller) = Start_();
        await scheduler.EnrolAsync(default);
        _clock.SetUtcNow(Start + PhaseOf(tenant));

        (await RunDueAsync(scheduler)).Should().Be(1);

        poller.Attempts.Should().Be(0);
        (await DueAtAsync(tenant)).Should().Be(_clock.GetUtcNow() + TimeSpan.FromHours(1));
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition did not hold within 30 seconds.");
            await Task.Delay(20);
        }
    }

    private static TimeSpan PhaseOf(Guid tenant) => JobPhase.Of(PollerName, tenant, TimeSpan.FromMinutes(1));

    private static string PollerName => new RecordingPoller(new ConnectorSyncBudget(), Mock.Of<IJobLedger>(), TimeProvider.System, false).Name;

    private static Guid TenantWithPhaseAtLeast(TimeSpan least)
    {
        while (true)
        {
            var tenant = Guid.CreateVersion7();
            if (PhaseOf(tenant) >= least)
                return tenant;
        }
    }

    private static (Guid Early, Guid Late) TwoTenantsWithApartPhases()
    {
        while (true)
        {
            Guid a = Guid.CreateVersion7(), b = Guid.CreateVersion7();
            var gap = PhaseOf(b) - PhaseOf(a);
            if (gap.Duration() >= TimeSpan.FromSeconds(5))
                return gap > TimeSpan.Zero ? (a, b) : (b, a);
        }
    }

    private async Task<int> RunDueAsync(JobScheduler scheduler)
    {
        var started = await scheduler.PollAsync(default);
        await scheduler.DrainAsync();
        return started;
    }

    private bool _configured = true;

    private (JobScheduler Scheduler, RecordingPoller Poller) Start_(bool fails = false, ConnectorPollerNudge? nudge = null)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        var options = new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql(_app).Options;
        services.AddSingleton<IDbContextFactory<NocturneDbContext>>(new Contexts(options));
        services.AddSingleton(sp => sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>().AsNocturneFactory());
        services.AddSingleton<TimeProvider>(_clock);
        services.AddTransient<IJobLedger, JobLedgerRepository>();
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddSingleton<IConnectorTokenCache>(new ConnectorTokenCache());
        services.AddScoped(_ => Mock.Of<IConnectorConfigurationService>());
        services.AddScoped<IConnectorConfigurationLoader<PollerConfig>>(_ => new SwitchedLoader(() => _configured));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        var poller = new RecordingPoller(new ConnectorSyncBudget(), provider.GetRequiredService<IJobLedger>(), _clock, fails, nudge);
        var scheduler = new JobScheduler(
            [poller],
            provider.GetRequiredService<IJobLedger>(),
            provider,
            _clock,
            new JobMetrics(provider.GetRequiredService<IMeterFactory>()),
            Options.Create(new JobSchedulerOptions()),
            NullLogger<JobScheduler>.Instance);
        return (scheduler, poller);
    }

    private IJobLedger Ledger() => new JobLedgerRepository(
        new Contexts(new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql(_app).Options).AsNocturneFactory(),
        _clock);

    private async Task<JobLedgerEntry> RowAsync(Guid tenant) =>
        (await Ledger().ListAsync(100_000, default)).Rows.Single(r => r.Job == PollerName && r.TenantId == tenant);

    private async Task<DateTimeOffset> DueAtAsync(Guid tenant) => (await RowAsync(tenant)).NextDueAt!.Value;

    private async Task SeedTenantAsync(Guid tenant, bool active = true)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var insert = new NpgsqlCommand(
            "INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES (@id, @slug, 'T', @active, now(), now())", conn);
        insert.Parameters.AddWithValue("id", tenant);
        insert.Parameters.AddWithValue("slug", tenant.ToString("N"));
        insert.Parameters.AddWithValue("active", active);
        await insert.ExecuteNonQueryAsync();
    }

    private sealed class Contexts(DbContextOptions<NocturneDbContext> options) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }

    [ConnectorRegistration("PollerTest", "poller-test", "POLLERTEST", "PollerTest")]
    private sealed class PollerConfig : BaseConnectorConfiguration;

    private sealed class SwitchedLoader(Func<bool> configured) : IConnectorConfigurationLoader<PollerConfig>
    {
        public Task<PollerConfig> LoadForTenantAsync(CancellationToken ct) =>
            Task.FromResult(new PollerConfig { Enabled = configured(), SyncIntervalMinutes = (int)Interval.TotalMinutes });
    }

    /// <summary>Records each tenant's sync and the fake time it ran at, or fails every one.</summary>
    private sealed class RecordingPoller : ConnectorBackgroundService<PollerConfig>
    {
        private readonly TimeProvider _clock;
        private readonly bool _fails;
        private int _attempts;

        public RecordingPoller(
            ConnectorSyncBudget budget, IJobLedger ledger, TimeProvider clock, bool fails, ConnectorPollerNudge? nudge = null)
            : base(budget, ledger, NullLogger.Instance, clock, nudge)
        {
            _clock = clock;
            _fails = fails;
        }

        public ConcurrentQueue<(Guid Tenant, DateTimeOffset At)> Syncs { get; } = new();

        public int Attempts => Volatile.Read(ref _attempts);

        public void ListenerEvent(Guid tenant) => RequestImmediateSync(tenant);

        protected override Task<SyncResult> PerformSyncAsync(
            IServiceProvider scopeProvider,
            PollerConfig config,
            CancellationToken cancellationToken,
            ISyncProgressReporter? progressReporter = null)
        {
            Interlocked.Increment(ref _attempts);
            if (_fails)
                throw new HttpRequestException("upstream refused");

            Syncs.Enqueue((scopeProvider.GetRequiredService<ITenantAccessor>().TenantId, _clock.GetUtcNow()));
            return Task.FromResult(new SyncResult { Success = true });
        }
    }
}
