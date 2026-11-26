using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Audit;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// A glucose write's evaluation that never completes is made good by the next sweep past
/// <see cref="AlertSweepJob.LostEvaluationGrace"/>, through the write path's own evaluator, and a
/// reading whose evaluation completed is not evaluated again (docs/alerts/engine-semantics.md §5.2).
/// On PostgreSQL under the runtime role and Row Level Security, since the record of the evaluated
/// reading and the tracker are what a restarted process reads.
/// </summary>
[Trait("Category", "Integration")]
public class LostEvaluationPostgresTests(LostEvaluationPostgresTests.Database database)
    : IClassFixture<LostEvaluationPostgresTests.Database>
{
    private static readonly DateTime Stored = new(2026, 11, 23, 3, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan PastGrace = AlertSweepJob.LostEvaluationGrace + TimeSpan.FromSeconds(1);
    private const string LostWarning = "No evaluation of the reading";
    private const string FirstRecord = "no evaluation was recorded for the tenant before";
    private const string Low = """{"direction":"below","value":70}""";

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("lost_evaluation");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// An engine that can fail the next evaluation, or hold every evaluation until released, as a
    /// pass still running or cut off does.
    /// </summary>
    private sealed class Interrupting(IAlertEvaluationEngine inner, Tenant tenant) : IAlertEvaluationEngine
    {
        public async Task<AlertEngineEvaluation> EvaluateRuleAsync(
            AlertRuleSnapshot rule, SensorContext context, AlertEngineOptions options, CancellationToken ct)
        {
            if (tenant.Hold is { } hold)
                await hold.Task.WaitAsync(ct);
            if (tenant.AlwaysThrow || Interlocked.Exchange(ref tenant.ThrowNext, 0) == 1)
                throw new InvalidOperationException("The engine failed");
            return await inner.EvaluateRuleAsync(rule, context, options, ct);
        }

        public Task<bool> EvaluateSnoozeAsync(Guid ruleId, JsonElement conditions, SensorContext context, CancellationToken ct) =>
            inner.EvaluateSnoozeAsync(ruleId, conditions, context, ct);

        public Task<ExcursionTransition> EvaluateAutoResolveAsync(AlertRuleSnapshot rule, SensorContext context, CancellationToken ct) =>
            inner.EvaluateAutoResolveAsync(rule, context, ct);
    }

    /// <summary>Counts every pass and the reading time its context measured.</summary>
    private sealed class CountingOrchestrator(AlertOrchestrator inner, Tenant tenant) : IAlertOrchestrator
    {
        public Task<AlertPassOutcome> EvaluateRulesAsync(
            IReadOnlyList<AlertRuleSnapshot> rules, IReadOnlySet<Guid> enabledRuleIds, SensorContext context,
            long? readAt, CancellationToken ct)
        {
            lock (tenant.Passes)
                tenant.Passes.Add(context.LatestTimestamp);
            return inner.EvaluateRulesAsync(rules, enabledRuleIds, context, readAt, ct);
        }
    }

    private sealed class CountingStore(IEvaluatedReadingStore inner, Tenant tenant) : IEvaluatedReadingStore
    {
        public Task<EvaluatedReading?> GetAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref tenant.MarkerReads);
            return inner.GetAsync(ct);
        }

        public Task RecordAsync(EvaluatedReading reading, CancellationToken ct) => inner.RecordAsync(reading, ct);
    }

    /// <summary>
    /// One tenant with one enabled low rule, and the pieces of an API process over the shared
    /// database: each <see cref="Process"/> has its own watermark, decision record and sweep, as a
    /// replica or a restarted process does.
    /// </summary>
    private sealed class Tenant
    {
        public required Guid Id { get; init; }
        public required Guid RuleId { get; init; }
        public required Func<NocturneDbContext> Context { get; init; }
        public readonly FakeTimeProvider Time = new(Stored);
        public readonly List<SensorGlucose> Readings = [];
        public readonly List<DateTime?> Passes = [];
        public readonly Mock<ICanonicalGlucoseService> Canonical = new();
        public readonly ListLogger<AlertSweepJob> SweepLog = new();
        public int Openings;
        public int MarkerReads;
        public int ThrowNext;
        public bool AlwaysThrow;
        public TaskCompletionSource? Hold;

        public Tenant()
        {
            Canonical
                .Setup(c => c.GetLatestAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Readings.MaxBy(r => r.Timestamp));
            Canonical
                .Setup(c => c.GetLatestAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DateTime notAfter, CancellationToken _) =>
                    Readings.Where(r => r.Timestamp <= notAfter).MaxBy(r => r.Timestamp));
            Canonical
                .Setup(c => c.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DateTime since, CancellationToken _) =>
                    Readings.Where(r => r.Timestamp >= since).OrderByDescending(r => r.Timestamp).ToList());
            Canonical
                .Setup(c => c.GetStreamPredecessorAsync(It.IsAny<SensorGlucose>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SensorGlucose reading, DateTime since, CancellationToken _) => Readings
                    .Where(r => r.Timestamp < reading.Timestamp && r.Timestamp >= since)
                    .MaxBy(r => r.Timestamp));
        }

        /// <summary>A reading stored at <paramref name="storedAt"/>, as the write commits it, with no evaluation yet.</summary>
        public SensorGlucose Store(double mgdl, DateTime? storedAt = null, DateTime? at = null, double? trendRate = null)
        {
            var reading = new SensorGlucose
            {
                Id = Guid.CreateVersion7(),
                Timestamp = at ?? storedAt ?? Stored,
                CreatedAt = storedAt ?? Stored,
                Mgdl = mgdl,
                TrendRate = trendRate,
            };
            Readings.Add(reading);
            return reading;
        }

        public Process Process() => new(this);

        public int PassesOf(SensorGlucose reading)
        {
            lock (Passes)
                return Passes.Count(at => at == reading.Timestamp);
        }

        public async Task<EvaluatedReading?> RecordedAsync() =>
            await new AlertEvaluatedReadingRepository(new SuppliedContextFactory(Context)).GetAsync(CancellationToken.None);

        public async Task<DateTime?> HeldSinceAsync()
        {
            await using var db = Context();
            return await db.AlertTrackerState
                .Where(t => t.AlertRuleId == RuleId)
                .Select(t => t.HeldSince)
                .SingleOrDefaultAsync();
        }

        public async Task<int> ExcursionsAsync()
        {
            await using var db = Context();
            return await db.AlertExcursions.CountAsync(e => e.AlertRuleId == RuleId);
        }

        public async Task SetEnabledAsync(bool enabled)
        {
            await using var db = Context();
            var rule = await db.AlertRules.SingleAsync(r => r.Id == RuleId);
            rule.IsEnabled = enabled;
            await db.SaveChangesAsync();
        }
    }

    private sealed class Process
    {
        private readonly Tenant _tenant;
        private readonly ServiceProvider _services;
        private readonly AlertSweepJob _sweep;

        public Process(Tenant tenant)
        {
            _tenant = tenant;
            var factory = new SuppliedContextFactory(tenant.Context);
            var opened = new Mock<IExcursionOpenedHandler>();
            opened
                .Setup(h => h.HandleOpenedAsync(
                    It.IsAny<AlertRuleSnapshot>(), It.IsAny<Guid>(), It.IsAny<SensorContext>(), It.IsAny<Guid>(),
                    It.IsAny<bool>(), It.IsAny<IReadOnlyList<AlertMetPoint>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Callback(() => Interlocked.Increment(ref tenant.Openings))
                .Returns(Task.CompletedTask);
            var enricher = new Mock<ISensorContextEnricher>();
            enricher
                .Setup(e => e.EnrichAsync(It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SensorContext c, IEnumerable<AlertRuleSnapshot> _, CancellationToken _) => c);

            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddMetrics();
            services.AddSingleton<TimeProvider>(tenant.Time);
            services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
            services.AddScoped<IAuditContext, AuditContext>();
            services.AddSingleton<INocturneDbContextFactory>(factory);
            services.AddSingleton<IAlertRepository>(new AlertRepository(factory));
            services.AddSingleton<IAlertTrackerRepository>(new AlertTrackerRepository(factory));
            services.AddSingleton<IEvaluatedReadingStore>(
                new CountingStore(new AlertEvaluatedReadingRepository(factory), tenant));
            services.AddSingleton(tenant.Canonical.Object);
            services.AddSingleton(enricher.Object);
            services.AddSingleton(opened.Object);
            services.AddSingleton(Mock.Of<IExcursionResolutionHandler>());
            services.AddSingleton(Mock.Of<IUnevaluableRuleNotice>());
            services.AddSingleton(Mock.Of<IClockAnomalyNotice>());
            services.AddSingleton(Mock.Of<IAlertChannelNotice>());
            services.AddSingleton(NoHeldRules.Notice());
            services.AddSingleton<AlertEngineErrors>();
            services.AddSingleton<ConditionVersionLog>();
            services.AddSingleton<NotTrueRecordingFloor>();
            services.AddSingleton<AlertRuleEvaluationGate>();
            services.AddSingleton<AlertRuleDecisions>();
            services.AddSingleton<AlertEvaluationWatermark>();
            services.AddScoped<RustBackedAlertEngine>();
            services.AddScoped<IAlertEvaluationEngine>(sp => new Interrupting(sp.GetRequiredService<RustBackedAlertEngine>(), tenant));
            services.AddScoped<AlertOrchestrator>();
            services.AddScoped<IAlertOrchestrator>(sp => new CountingOrchestrator(sp.GetRequiredService<AlertOrchestrator>(), tenant));
            services.AddScoped<ICanonicalAlertEvaluator, CanonicalAlertEvaluator>();
            _services = services.BuildServiceProvider();
            _sweep = new AlertSweepJob(tenant.SweepLog);
        }

        public AlertEvaluationWatermark Watermark => _services.GetRequiredService<AlertEvaluationWatermark>();

        /// <summary>The evaluation a committed glucose write starts, as the scheduler runs it.</summary>
        public async Task WriteEvaluationAsync(CancellationToken ct = default)
        {
            var context = new TenantContext(_tenant.Id, "slug", "Slug", true, IsDemo: false);
            await using var scope = TenantJobScope.Open(_services, context, AlertFactEvaluationScheduler.AuditEndpoint);
            await scope.ServiceProvider.GetRequiredService<ICanonicalAlertEvaluator>()
                .EvaluateAsync(new MovedFacts(new HashSet<string> { ContextFactWrites.Glucose }), ct: ct);
        }

        public Task SweepAsync(DateTime at)
        {
            _tenant.Time.SetUtcNow(new DateTimeOffset(at));
            return _sweep.RunAsync(_services, _tenant.Id, new DateTimeOffset(at));
        }
    }

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant);

    private async Task<Tenant> SeedAsync(
        bool withRule = true, AlertConditionType condition = AlertConditionType.Threshold, string conditionParams = Low)
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'lost-evaluation', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"lost-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var rule = Guid.CreateVersion7();
        if (withRule)
        {
            await using var seed = Context(tenant);
            seed.AlertRules.Add(new AlertRuleEntity
            {
                Id = rule,
                TenantId = tenant,
                Name = "Low",
                ConditionType = condition,
                ConditionParams = conditionParams,
                Severity = AlertRuleSeverity.Critical,
                IsEnabled = true,
                CreatedAt = Stored.AddDays(-1),
                UpdatedAt = Stored.AddDays(-1),
            });
            await seed.SaveChangesAsync();
        }

        return new Tenant { Id = tenant, RuleId = rule, Context = () => Context(tenant) };
    }

    private static int Warnings(Tenant tenant) => tenant.SweepLog.Warnings.Count(w => w.Contains(LostWarning));

    private static int FirstRecords(Tenant tenant) => tenant.SweepLog.Entries.Count(e =>
        e.Level == LogLevel.Information && e.Message.Contains(FirstRecord));

    private static int LostLogs(Tenant tenant) => Warnings(tenant) + FirstRecords(tenant);

    [NativeFact]
    public async Task An_evaluation_lost_before_it_ran_is_made_good_by_the_first_sweep_past_the_grace()
    {
        var tenant = await SeedAsync();
        var low = tenant.Store(mgdl: 55);
        var process = tenant.Process();

        await process.SweepAsync(Stored + AlertSweepJob.LostEvaluationGrace - TimeSpan.FromSeconds(1));
        tenant.PassesOf(low).Should().Be(0, "the write's own evaluation normally completes inside the grace");

        await process.SweepAsync(Stored + PastGrace);
        tenant.PassesOf(low).Should().Be(1);
        tenant.Openings.Should().Be(1, "the low opens as the lost evaluation would have opened it");
        (await tenant.ExcursionsAsync()).Should().Be(1);
        (await tenant.RecordedAsync()).Should().Be(EvaluatedReading.Of(low));

        await process.SweepAsync(Stored + PastGrace + AlertSweepJob.Interval);
        tenant.PassesOf(low).Should().Be(1, "the reading is recorded as evaluated");
    }

    [NativeFact]
    public async Task A_write_evaluation_that_completed_is_not_evaluated_again_by_any_sweep()
    {
        var tenant = await SeedAsync();
        var low = tenant.Store(mgdl: 55);
        var process = tenant.Process();

        await process.WriteEvaluationAsync();
        await process.SweepAsync(Stored + PastGrace);
        await process.SweepAsync(Stored + PastGrace + AlertSweepJob.Interval);
        await tenant.Process().SweepAsync(Stored + PastGrace + 2 * AlertSweepJob.Interval);

        tenant.PassesOf(low).Should().Be(1);
        tenant.Openings.Should().Be(1);
    }

    [NativeFact]
    public async Task A_sweep_inside_the_grace_then_the_write_evaluates_the_reading_once()
    {
        var tenant = await SeedAsync();
        var low = tenant.Store(mgdl: 55);
        var process = tenant.Process();

        await process.SweepAsync(Stored + TimeSpan.FromSeconds(2));
        await process.WriteEvaluationAsync();
        await process.SweepAsync(Stored + PastGrace);

        tenant.PassesOf(low).Should().Be(1);
    }

    [NativeFact]
    public async Task A_late_write_evaluation_after_the_sweep_made_it_good_does_not_evaluate_again()
    {
        var tenant = await SeedAsync();
        var low = tenant.Store(mgdl: 55);
        var process = tenant.Process();

        await process.SweepAsync(Stored + PastGrace);
        await process.WriteEvaluationAsync();

        tenant.PassesOf(low).Should().Be(1, "the sweep's pass claimed the reading in this process");
        tenant.Openings.Should().Be(1);
    }

    [NativeFact]
    public async Task A_write_evaluation_still_running_past_the_grace_is_not_overlapped_in_its_own_process()
    {
        var tenant = await SeedAsync();
        var low = tenant.Store(mgdl: 55);
        var process = tenant.Process();
        tenant.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var write = process.WriteEvaluationAsync();
        await process.SweepAsync(Stored + PastGrace);
        tenant.PassesOf(low).Should().Be(1, "the running pass holds the reading's claim");
        LostLogs(tenant).Should().Be(0, "a sweep refused the claim evaluated nothing");

        tenant.Hold.SetResult();
        await write;
        tenant.Openings.Should().Be(1);
        (await tenant.RecordedAsync()).Should().Be(EvaluatedReading.Of(low));
    }

    [NativeFact]
    public async Task A_write_evaluation_running_in_another_process_past_the_grace_is_evaluated_twice_and_opens_once()
    {
        var tenant = await SeedAsync();
        var low = tenant.Store(mgdl: 55);
        var writer = tenant.Process();
        var sweeper = tenant.Process();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tenant.Hold = hold;

        var write = writer.WriteEvaluationAsync();
        await WaitForPassAsync(tenant, low);
        tenant.Hold = null;
        await sweeper.SweepAsync(Stored + PastGrace);
        tenant.Openings.Should().Be(1);

        hold.SetResult();
        await write;

        tenant.PassesOf(low).Should().Be(2, "the other process's claim is not seen");
        tenant.Openings.Should().Be(1, "the second decision finds the excursion open and continues it");
        (await tenant.ExcursionsAsync()).Should().Be(1);
    }

    [NativeFact]
    public async Task A_rule_whose_evaluation_threw_is_evaluated_by_the_next_sweep()
    {
        var tenant = await SeedAsync();
        var low = tenant.Store(mgdl: 55);
        var process = tenant.Process();
        tenant.ThrowNext = 1;

        await process.WriteEvaluationAsync();
        tenant.Openings.Should().Be(0);
        (await tenant.RecordedAsync()).Should().BeNull("a pass in which a rule threw records nothing");

        await process.SweepAsync(Stored + PastGrace);
        tenant.PassesOf(low).Should().Be(2);
        tenant.Openings.Should().Be(1);
        (await tenant.RecordedAsync()).Should().Be(EvaluatedReading.Of(low));
    }

    [NativeFact]
    public async Task An_evaluation_abandoned_at_shutdown_is_evaluated_by_the_next_sweep()
    {
        var tenant = await SeedAsync();
        var low = tenant.Store(mgdl: 55);
        var process = tenant.Process();
        tenant.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var abandon = new CancellationTokenSource();

        var write = process.WriteEvaluationAsync(abandon.Token);
        await WaitForPassAsync(tenant, low);
        tenant.Hold = null;
        await abandon.CancelAsync();
        await write.Awaiting(t => t).Should().ThrowAsync<OperationCanceledException>("the shutdown stops the pass");
        (await tenant.RecordedAsync()).Should().BeNull();

        await process.SweepAsync(Stored + PastGrace);
        tenant.PassesOf(low).Should().Be(2);
        tenant.Openings.Should().Be(1);
    }

    [NativeFact]
    public async Task A_new_process_evaluates_a_reading_a_dead_process_claimed_and_then_nothing_more()
    {
        var tenant = await SeedAsync();
        var low = tenant.Store(mgdl: 55);
        tenant.Process().Watermark.TryClaim(tenant.Id, low).Should().BeTrue("the dead process had begun its pass");

        await tenant.Process().SweepAsync(Stored + PastGrace);
        tenant.PassesOf(low).Should().Be(1);
        tenant.Openings.Should().Be(1);

        await tenant.Process().SweepAsync(Stored + PastGrace + AlertSweepJob.Interval);
        tenant.PassesOf(low).Should().Be(1, "a process that starts after the reading was recorded evaluates nothing");
    }

    [NativeFact]
    public async Task A_reading_whose_every_pass_fails_is_retried_each_sweep_and_warned_of_once()
    {
        var tenant = await SeedAsync();
        var low = tenant.Store(mgdl: 55);
        var process = tenant.Process();
        tenant.AlwaysThrow = true;

        for (var run = 0; run < 3; run++)
        {
            var at = Stored + PastGrace + run * AlertSweepJob.Interval;
            await process.Invoking(p => p.SweepAsync(at)).Should().NotThrowAsync("the tenant's other passes go on");
        }

        tenant.PassesOf(low).Should().Be(3);
        FirstRecords(tenant).Should().Be(1, "a tenant with no record yet is no sign of a loss");
        Warnings(tenant).Should().Be(0);
        (await tenant.RecordedAsync()).Should().BeNull();

        tenant.AlwaysThrow = false;
        await process.SweepAsync(Stored + PastGrace + 3 * AlertSweepJob.Interval);
        tenant.Openings.Should().Be(1);
        var next = tenant.Store(mgdl: 54, storedAt: Stored.AddMinutes(5));
        tenant.AlwaysThrow = true;
        await process.SweepAsync(next.CreatedAt + PastGrace);
        await process.SweepAsync(next.CreatedAt + PastGrace + AlertSweepJob.Interval);
        tenant.PassesOf(next).Should().Be(2);
        Warnings(tenant).Should().Be(1, "a record of another reading is warned of, once per reading");
        FirstRecords(tenant).Should().Be(1);
    }

    [NativeFact]
    public async Task A_newest_reading_with_no_value_is_evaluated_once_and_recorded()
    {
        var tenant = await SeedAsync();
        tenant.Store(mgdl: 120, storedAt: Stored.AddMinutes(-5));
        var valueless = tenant.Store(mgdl: 0);
        var process = tenant.Process();

        await process.SweepAsync(Stored + PastGrace);
        await process.SweepAsync(Stored + PastGrace + AlertSweepJob.Interval);

        tenant.PassesOf(valueless).Should().Be(1, "the write path selects the reading with no value, so the sweep compares with it");
        (await tenant.RecordedAsync()).Should().Be(EvaluatedReading.Of(valueless));
    }

    [NativeFact]
    public async Task A_reading_stored_by_a_clock_ahead_of_the_sweeps_is_evaluated()
    {
        var tenant = await SeedAsync();
        var low = tenant.Store(mgdl: 55, storedAt: Stored.AddMinutes(1), at: Stored);
        var process = tenant.Process();

        await process.SweepAsync(Stored + TimeSpan.FromSeconds(5));

        tenant.PassesOf(low).Should().Be(1, "a stored time ahead of the clock cannot say the write's evaluation is still running");
    }

    [NativeFact]
    public async Task A_tenant_with_no_enabled_rule_reads_neither_its_readings_nor_its_record()
    {
        var tenant = await SeedAsync(withRule: false);
        tenant.Store(mgdl: 55);

        await tenant.Process().SweepAsync(Stored + PastGrace);

        tenant.Canonical.Verify(c => c.GetLatestAsync(It.IsAny<CancellationToken>()), Times.Never);
        tenant.MarkerReads.Should().Be(0);
    }

    [NativeFact]
    public async Task A_tenant_with_no_reading_reads_only_its_newest_reading()
    {
        var tenant = await SeedAsync();

        await tenant.Process().SweepAsync(Stored + PastGrace);

        tenant.Canonical.Verify(c => c.GetLatestAsync(It.IsAny<CancellationToken>()), Times.Once);
        tenant.MarkerReads.Should().Be(0);
        tenant.Passes.Should().BeEmpty();
    }

    [NativeFact]
    public async Task A_rule_enabled_after_its_reading_was_evaluated_without_it_is_evaluated_by_the_sweep()
    {
        var tenant = await SeedAsync();
        await tenant.SetEnabledAsync(false);
        var low = tenant.Store(mgdl: 55);
        var process = tenant.Process();
        await process.WriteEvaluationAsync();
        (await tenant.RecordedAsync()).Should().BeNull("a pass with no enabled rule decides nothing");

        await tenant.SetEnabledAsync(true);
        await process.SweepAsync(Stored + PastGrace + AlertEvaluationWatermark.MaxSkipWindow);

        tenant.PassesOf(low).Should().Be(1);
        tenant.Openings.Should().Be(1);
    }

    [NativeFact]
    public async Task A_value_corrected_at_the_same_timestamp_whose_evaluation_was_lost_is_evaluated_by_the_sweep()
    {
        var tenant = await SeedAsync();
        var reading = tenant.Store(mgdl: 120);
        var process = tenant.Process();
        await process.WriteEvaluationAsync();
        (await tenant.RecordedAsync()).Should().Be(EvaluatedReading.Of(reading));

        reading.Mgdl = 55;
        await process.SweepAsync(Stored + PastGrace);

        tenant.Openings.Should().Be(1, "the record names the reading's old value, not this one");
        (await tenant.RecordedAsync()).Should().Be(new EvaluatedReading(Stored, 55, null));
    }

    [NativeFact]
    public async Task A_trend_rate_corrected_at_the_same_timestamp_whose_evaluation_was_lost_is_evaluated_by_the_sweep()
    {
        var tenant = await SeedAsync(
            condition: AlertConditionType.RateOfChange, conditionParams: """{"direction":"falling","rate":2}""");
        var reading = tenant.Store(mgdl: 100, trendRate: -0.5);
        var process = tenant.Process();
        await process.WriteEvaluationAsync();
        tenant.Openings.Should().Be(0);
        (await tenant.RecordedAsync()).Should().Be(EvaluatedReading.Of(reading));

        reading.TrendRate = -3;
        await process.SweepAsync(Stored + PastGrace);

        tenant.Openings.Should().Be(1, "the record names the reading's old trend rate, not this one");
        (await tenant.RecordedAsync()).Should().Be(new EvaluatedReading(Stored, 100, -3));
    }

    [NativeFact]
    public async Task A_repeat_refused_while_the_claiming_pass_runs_records_nothing_and_the_sweep_makes_the_failed_pass_good()
    {
        var tenant = await SeedAsync();
        var low = tenant.Store(mgdl: 55);
        var process = tenant.Process();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tenant.Hold = hold;

        var write = process.WriteEvaluationAsync();
        await WaitForPassAsync(tenant, low);

        await process.WriteEvaluationAsync();
        tenant.PassesOf(low).Should().Be(1, "the repeat is refused the claim");
        (await tenant.RecordedAsync()).Should().BeNull("a refused repeat decided nothing");

        tenant.ThrowNext = 1;
        hold.SetResult();
        await write;
        tenant.Hold = null;
        (await tenant.RecordedAsync()).Should().BeNull("the claiming pass threw");

        await process.SweepAsync(Stored + PastGrace);
        tenant.PassesOf(low).Should().Be(2, "the throw released the claim");
        tenant.Openings.Should().Be(1);
        (await tenant.RecordedAsync()).Should().Be(EvaluatedReading.Of(low));
    }

    [NativeFact]
    public async Task A_sweep_refused_the_claim_of_a_reading_after_a_recorded_one_logs_nothing()
    {
        var tenant = await SeedAsync();
        tenant.Store(mgdl: 120, storedAt: Stored.AddMinutes(-5));
        var process = tenant.Process();
        await process.WriteEvaluationAsync();
        var low = tenant.Store(mgdl: 55);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tenant.Hold = hold;

        var write = process.WriteEvaluationAsync();
        await WaitForPassAsync(tenant, low);
        await process.SweepAsync(Stored + PastGrace);
        LostLogs(tenant).Should().Be(0, "the write's pass still holds the claim, so the sweep evaluated nothing");

        hold.SetResult();
        await write;
        tenant.Hold = null;
        await process.SweepAsync(Stored + PastGrace + AlertSweepJob.Interval);

        tenant.PassesOf(low).Should().Be(1);
        LostLogs(tenant).Should().Be(0);
    }

    [NativeFact]
    public async Task A_reading_stored_longer_ago_than_any_rule_can_read_it_is_never_evaluated_or_recorded()
    {
        var tenant = await SeedAsync();
        var old = tenant.Store(mgdl: 55, storedAt: Stored.AddDays(-3));
        var process = tenant.Process();

        await process.SweepAsync(Stored);
        await process.SweepAsync(Stored + AlertSweepJob.Interval);
        await tenant.Process().SweepAsync(Stored + AlertSweepJob.Interval + TimeSpan.FromDays(1));

        tenant.PassesOf(old).Should().Be(0, "every glucose leaf reads it as stale, so its evaluation could open nothing");
        (await tenant.HeldSinceAsync()).Should().BeNull("no evaluation starts a hold nothing started before");
        (await tenant.RecordedAsync()).Should().BeNull();
        tenant.MarkerReads.Should().Be(0);
        LostLogs(tenant).Should().Be(0);
    }

    [NativeFact]
    public async Task A_reading_stored_exactly_the_largest_freshness_bound_ago_is_evaluated()
    {
        var tenant = await SeedAsync();
        var old = tenant.Store(mgdl: 55, storedAt: Stored - AlertSweepJob.LostEvaluationReach);

        await tenant.Process().SweepAsync(Stored);

        tenant.PassesOf(old).Should().Be(1, "a rule whose bound is the largest still reads it as fresh");
        (await tenant.HeldSinceAsync()).Should().Be(Stored, "this rule's own bound is the default, so it reads the reading as stale and holds");
        (await tenant.RecordedAsync()).Should().Be(EvaluatedReading.Of(old));
    }

    [NativeFact]
    public async Task A_reading_taken_long_ago_and_stored_a_minute_ago_is_evaluated()
    {
        var tenant = await SeedAsync();
        var late = tenant.Store(mgdl: 55, storedAt: Stored.AddMinutes(-1), at: Stored.AddDays(-3));

        await tenant.Process().SweepAsync(Stored);

        tenant.PassesOf(late).Should().Be(1, "a reading's age runs from its arrival, so it is fresh");
        tenant.Openings.Should().Be(1);
        (await tenant.RecordedAsync()).Should().Be(EvaluatedReading.Of(late));
    }

    [NativeFact]
    public async Task The_first_evaluation_recorded_for_a_tenant_is_logged_as_information_once()
    {
        var tenant = await SeedAsync();
        tenant.Store(mgdl: 55);
        var process = tenant.Process();

        await process.SweepAsync(Stored + PastGrace);
        await process.SweepAsync(Stored + PastGrace + AlertSweepJob.Interval);

        FirstRecords(tenant).Should().Be(1);
        Warnings(tenant).Should().Be(0, "a tenant with no record has lost nothing it can name");
    }

    [NativeTheory]
    [InlineData("condition_type", "threshold_low", "threshold")]
    [InlineData("severity", "normal", "critical")]
    [InlineData("scope_class", "sideways", "undirected")]
    public async Task A_rule_holding_a_value_no_member_has_never_fires_until_it_holds_a_readable_one(
        string column, string unreadable, string readable)
    {
        var tenant = await SeedAsync();
        await SetRuleColumnAsync(tenant, column, unreadable);
        tenant.Store(mgdl: 55);
        var process = tenant.Process();

        await process.WriteEvaluationAsync();
        await process.SweepAsync(Stored + PastGrace);

        tenant.Openings.Should().Be(0);
        (await tenant.ExcursionsAsync()).Should().Be(0);

        await SetRuleColumnAsync(tenant, column, readable);
        tenant.Store(mgdl: 54, storedAt: Stored + PastGrace);
        await process.WriteEvaluationAsync();

        tenant.Openings.Should().Be(1, "the same rule fires once its value is readable");
    }

    private async Task SetRuleColumnAsync(Tenant tenant, string column, string value)
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT set_config('app.current_tenant_id', '{tenant.Id}', false);
            UPDATE alert_rules SET {column} = @value WHERE id = '{tenant.RuleId}';
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@value", value));
        await cmd.ExecuteNonQueryAsync();
    }

    private static Task WaitForPassAsync(Tenant tenant, SensorGlucose reading) =>
        Eventually.UntilAsync("the write's pass to begin", () => tenant.PassesOf(reading) > 0);
}
