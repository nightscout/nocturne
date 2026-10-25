using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Tests.Services.Alerts.Engines;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// On PostgreSQL under the runtime role and Row Level Security, an edit on another replica lands
/// after an evaluation loaded the rule and read its tracker state, and before the evaluation takes
/// the rule's transition lock. The evaluation's decision was made on the old rule, so it writes
/// nothing, and in particular sets no re-arm hold the edit cleared (docs/alerts/engine-semantics.md §6.3)
/// and no sustained timer the old tree set.
/// </summary>
[Trait("Category", "Integration")]
public class StaleRuleDecisionPostgresTests(StaleRuleDecisionPostgresTests.Database database)
    : IClassFixture<StaleRuleDecisionPostgresTests.Database>
{
    private const string Body = """{"direction":"below","value":70}""";
    private const string EditedBody = """{"direction":"below","value":60}""";
    private const string Resolve = """{"type":"threshold","threshold":{"direction":"below","value":90}}""";
    private const string SustainedBody = """{"minutes":10,"child":{"type":"threshold","threshold":{"direction":"below","value":70}}}""";
    private const string EditedSustainedBody = """{"minutes":15,"child":{"type":"threshold","threshold":{"direction":"below","value":70}}}""";
    private const string SustainedResolve = """{"type":"sustained","sustained":{"minutes":5,"child":{"type":"threshold","threshold":{"direction":"above","value":90}}}}""";
    private const string EditedSustainedResolve = """{"type":"sustained","sustained":{"minutes":6,"child":{"type":"threshold","threshold":{"direction":"above","value":90}}}}""";
    private const string SustainedSnooze = """{"snooze":{"smartSnooze":true,"conditions":[{"type":"sustained","sustained":{"minutes":5,"child":{"type":"threshold","threshold":{"direction":"above","value":90}}}}]}}""";
    private const string SustainedSnoozeConditions = """[{"type":"sustained","sustained":{"minutes":5,"child":{"type":"threshold","threshold":{"direction":"above","value":90}}}}]""";
    private const string LongerSustainedSnooze = """{"snooze":{"smartSnooze":true,"conditions":[{"type":"sustained","sustained":{"minutes":6,"child":{"type":"threshold","threshold":{"direction":"above","value":90}}}}]}}""";
    private const string ReorderedSnooze = """{"snooze":{"smartSnooze":true,"conditions":[{"type":"threshold","threshold":{"direction":"above","value":90}},{"type":"sustained","sustained":{"minutes":5,"child":{"type":"threshold","threshold":{"direction":"above","value":90}}}}]}}""";
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("stale_rule_decision");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>Runs <paramref name="edit"/> once, before the first transition lock is taken.</summary>
    private sealed class EditBeforeLockRepository(NocturneDbContext context, Func<Task> edit)
        : AlertTrackerRepository(DbUnitOfWork.Own(context))
    {
        private Func<Task>? _edit = edit;

        public override async Task LockRuleAsync(Guid alertRuleId, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _edit, null) is { } pending)
                await pending();
            await base.LockRuleAsync(alertRuleId, ct);
        }
    }

    private NocturneDbContext Context(Guid tenant) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options) { TenantId = tenant };

    private async Task<(Guid Tenant, AlertRuleSnapshot Rule, Guid? ExcursionId)> SeedAsync(
        bool active,
        AlertConditionType conditionType = AlertConditionType.Threshold,
        string body = Body,
        string resolve = Resolve,
        string clientConfiguration = "{}")
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'stale-rule-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"stale-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var rule = Guid.CreateVersion7();
        Guid? excursionId = active ? Guid.CreateVersion7() : null;
        await using (var seed = Context(tenant))
        {
            seed.AlertRules.Add(new AlertRuleEntity
            {
                Id = rule,
                TenantId = tenant,
                Name = "Low",
                ConditionType = conditionType,
                ConditionParams = body,
                ClientConfiguration = clientConfiguration,
                Severity = AlertRuleSeverity.Warning,
                IsEnabled = true,
                AutoResolveEnabled = true,
                AutoResolveParams = resolve,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            if (excursionId is { } id)
            {
                seed.AlertExcursions.Add(new AlertExcursionEntity
                {
                    Id = id,
                    TenantId = tenant,
                    AlertRuleId = rule,
                    StartedAt = Now.UtcDateTime.AddMinutes(-5),
                });
                seed.AlertTrackerState.Add(new AlertTrackerStateEntity
                {
                    AlertRuleId = rule,
                    TenantId = tenant,
                    State = "active",
                    ActiveExcursionId = id,
                    UpdatedAt = Now.UtcDateTime.AddMinutes(-5),
                });
            }
            await seed.SaveChangesAsync();
        }

        var snapshot = new AlertRuleSnapshot(
            rule, tenant, "Low", conditionType, body, AlertRuleSeverity.Warning, clientConfiguration, 0,
            AutoResolveEnabled: true, AutoResolveParams: resolve);
        return (tenant, snapshot, excursionId);
    }

    /// <summary>The conditions edit an alert rule editor saves, with its own lease, on another replica.</summary>
    private Func<Task> EditOnAnotherReplica(Guid tenant, Guid rule, Action<AlertRuleEntity>? edit = null) => async () =>
    {
        await using var unit = DbUnitOfWork.Own(Context(tenant));
        var stored = await unit.Context.AlertRules.SingleAsync(r => r.Id == rule);
        (edit ?? (r => r.ConditionParams = EditedBody))(stored);
        stored.UpdatedAt = DateTime.UtcNow;
        await new AlertRuleRearm(new AlertRuleEvaluationGate())
            .SaveAndClearAsync(unit, rule, CancellationToken.None);
    };

    /// <summary>An edit of the smart-snooze conditions alone, saved as the alert rule editor saves it, on another replica.</summary>
    private Func<Task> EditSnoozeOnAnotherReplica(Guid tenant, Guid rule, string clientConfiguration) => async () =>
    {
        await using var unit = DbUnitOfWork.Own(Context(tenant));
        var stored = await unit.Context.AlertRules.SingleAsync(r => r.Id == rule);
        stored.ClientConfiguration = clientConfiguration;
        stored.UpdatedAt = DateTime.UtcNow;
        await new AlertRuleRearm(new AlertRuleEvaluationGate())
            .SaveAndPruneAsync(unit, rule, CancellationToken.None);
    };

    private static JsonElement SnoozeConditions => JsonDocument.Parse(SustainedSnoozeConditions).RootElement;

    private static ExcursionTracker Tracker(AlertTrackerRepository repository) =>
        EngineTestHarness.BuildRustTracker(new FakeTimeProvider(Now), repository);

    private static RustBackedAlertEngine Engine(AlertTrackerRepository repository)
    {
        var time = new ManualTimeProvider();
        time.SetUtcNow(Now.UtcDateTime);
        return EngineTestHarness.BuildRustEngine(time, repository);
    }

    private static SensorContext Glucose(decimal mgdl) => new()
    {
        LatestValue = mgdl,
        LatestTimestamp = Now.UtcDateTime,
        TrendRate = null,
        LastReadingAt = Now.UtcDateTime,
    };

    private async Task<Dictionary<string, DateTime>> TimersAsync(Guid tenant)
    {
        await using var db = Context(tenant);
        return await db.AlertConditionTimers.AsNoTracking()
            .ToDictionaryAsync(t => t.ConditionPath, t => t.FirstTrueAt);
    }

    private async Task<(string State, Guid? ActiveExcursionId, bool AwaitingRearm, int OpenExcursions)> StoredAsync(Guid tenant)
    {
        await using var db = Context(tenant);
        var state = await db.AlertTrackerState.AsNoTracking().SingleOrDefaultAsync();
        var open = await db.AlertExcursions.AsNoTracking().CountAsync(e => e.EndedAt == null);
        return (state?.State ?? "none", state?.ActiveExcursionId, state?.AwaitingRearm ?? false, open);
    }

    [NativeFact]
    public async Task A_pre_edit_auto_resolve_close_sets_no_hold()
    {
        var (tenant, rule, excursionId) = await SeedAsync(active: true);
        await using var db = Context(tenant);
        // The evaluating context already holds the rule row, as a caller-owned unit can.
        await db.AlertRules.SingleAsync(r => r.Id == rule.Id);

        var transition = await Tracker(new EditBeforeLockRepository(db, EditOnAnotherReplica(tenant, rule.Id)))
            .ForceCloseAsync(rule, ExcursionCloseReason.AutoResolve, CancellationToken.None);

        transition.Type.Should().Be(ExcursionTransitionType.None);
        (await StoredAsync(tenant)).Should().Be(("active", excursionId, false, 1));
    }

    [NativeFact]
    public async Task A_pre_edit_evaluation_opens_nothing()
    {
        var (tenant, rule, _) = await SeedAsync(active: false);
        await using var db = Context(tenant);

        var transition = await Tracker(new EditBeforeLockRepository(db, EditOnAnotherReplica(tenant, rule.Id)))
            .ProcessEvaluationAsync(rule, conditionMet: true, null, CancellationToken.None);

        transition.Type.Should().Be(ExcursionTransitionType.None);
        (await StoredAsync(tenant)).Should().Be(("none", (Guid?)null, false, 0));
    }

    [NativeFact]
    public async Task An_auto_resolve_close_of_the_current_rule_sets_the_hold()
    {
        var (tenant, rule, excursionId) = await SeedAsync(active: true);
        await using var db = Context(tenant);

        var transition = await Tracker(new EditBeforeLockRepository(db, () => Task.CompletedTask))
            .ForceCloseAsync(rule, ExcursionCloseReason.AutoResolve, CancellationToken.None);

        transition.Should().Be(new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, excursionId, ExcursionCloseReason.AutoResolve));
        (await StoredAsync(tenant)).Should().Be(("idle", (Guid?)null, true, 0));
    }

    [NativeFact]
    public async Task A_close_no_evaluation_decided_is_written_after_an_edit()
    {
        var (tenant, rule, excursionId) = await SeedAsync(active: true);
        await using var db = Context(tenant);

        var transition = await Tracker(new EditBeforeLockRepository(db, EditOnAnotherReplica(tenant, rule.Id)))
            .ForceCloseAsync(rule.Id, ExcursionCloseReason.Manual, CancellationToken.None);

        transition.Should().Be(new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed, excursionId, ExcursionCloseReason.Manual));
        (await StoredAsync(tenant)).Should().Be(("idle", (Guid?)null, false, 0));
    }

    [NativeFact]
    public async Task A_pre_edit_evaluation_writes_no_sustained_timer()
    {
        var (tenant, rule, _) = await SeedAsync(active: false, AlertConditionType.Sustained, SustainedBody);
        await using var db = Context(tenant);
        var edit = EditOnAnotherReplica(tenant, rule.Id, r => r.ConditionParams = EditedSustainedBody);

        var evaluation = await Engine(new EditBeforeLockRepository(db, edit))
            .EvaluateRuleAsync(rule, Glucose(60), AlertEngineOptions.Default, CancellationToken.None);

        evaluation.Transition.Type.Should().Be(ExcursionTransitionType.None);
        (await TimersAsync(tenant)).Should().BeEmpty();
        (await StoredAsync(tenant)).Should().Be(("none", (Guid?)null, false, 0));
    }

    [NativeFact]
    public async Task An_evaluation_of_the_current_rule_writes_its_sustained_timer_with_its_tracker_state()
    {
        var (tenant, rule, _) = await SeedAsync(active: false, AlertConditionType.Sustained, SustainedBody);
        await using var db = Context(tenant);

        await Engine(new EditBeforeLockRepository(db, () => Task.CompletedTask))
            .EvaluateRuleAsync(rule, Glucose(60), AlertEngineOptions.Default, CancellationToken.None);

        (await TimersAsync(tenant)).Should().Equal(new Dictionary<string, DateTime> { ["sustained"] = Now.UtcDateTime });
        (await StoredAsync(tenant)).Should().Be(("idle", (Guid?)null, false, 0));
    }

    [NativeFact]
    public async Task An_evaluation_whose_tracker_state_another_process_changed_writes_no_sustained_timer()
    {
        var (tenant, rule, _) = await SeedAsync(active: false, AlertConditionType.Sustained, SustainedBody);
        await using var db = Context(tenant);
        var overlapping = async () =>
        {
            await using var other = Context(tenant);
            other.AlertTrackerState.Add(new AlertTrackerStateEntity
            {
                AlertRuleId = rule.Id,
                TenantId = tenant,
                State = "idle",
                UpdatedAt = Now.UtcDateTime.AddMinutes(-1),
            });
            await other.SaveChangesAsync();
        };

        var evaluation = await Engine(new EditBeforeLockRepository(db, overlapping))
            .EvaluateRuleAsync(rule, Glucose(60), AlertEngineOptions.Default, CancellationToken.None);

        evaluation.Transition.Type.Should().Be(ExcursionTransitionType.None);
        (await TimersAsync(tenant)).Should().BeEmpty();
        (await StoredAsync(tenant)).Should().Be(("idle", (Guid?)null, false, 0));
    }

    [NativeFact]
    public async Task A_pre_edit_auto_resolve_pass_writes_no_sustained_timer()
    {
        var (tenant, rule, excursionId) = await SeedAsync(active: true, resolve: SustainedResolve);
        await using var db = Context(tenant);
        var edit = EditOnAnotherReplica(tenant, rule.Id, r => r.AutoResolveParams = EditedSustainedResolve);

        var transition = await Engine(new EditBeforeLockRepository(db, edit))
            .EvaluateAutoResolveAsync(rule, Glucose(120), CancellationToken.None);

        transition.Type.Should().Be(ExcursionTransitionType.None);
        (await TimersAsync(tenant)).Should().BeEmpty();
        (await StoredAsync(tenant)).Should().Be(("active", excursionId, false, 1));
    }

    [NativeFact]
    public async Task An_auto_resolve_pass_of_the_current_rule_writes_its_sustained_timer()
    {
        var (tenant, rule, _) = await SeedAsync(active: true, resolve: SustainedResolve);
        await using var db = Context(tenant);

        await Engine(new EditBeforeLockRepository(db, () => Task.CompletedTask))
            .EvaluateAutoResolveAsync(rule, Glucose(120), CancellationToken.None);

        (await TimersAsync(tenant)).Should().Equal(new Dictionary<string, DateTime> { ["auto_resolve"] = Now.UtcDateTime });
    }

    [NativeFact]
    public async Task A_rule_edit_keeps_only_the_timers_its_trees_still_have()
    {
        var (tenant, rule, _) = await SeedAsync(
            active: false, AlertConditionType.Sustained, SustainedBody, SustainedResolve, SustainedSnooze);
        await using (var seed = Context(tenant))
        {
            foreach (var path in new[]
                     {
                         "sustained", "composite[0].sustained", "auto_resolve", "auto_resolve[0].sustained",
                         "snooze[0].sustained", "snooze[1].sustained",
                     })
            {
                seed.AlertConditionTimers.Add(new AlertConditionTimerEntity
                {
                    TenantId = tenant,
                    AlertRuleId = rule.Id,
                    ConditionPath = path,
                    FirstTrueAt = Now.UtcDateTime,
                });
            }
            await seed.SaveChangesAsync();
        }

        await EditOnAnotherReplica(tenant, rule.Id, r => r.ConditionParams = EditedSustainedBody)();

        (await TimersAsync(tenant)).Keys.Should().BeEquivalentTo("sustained", "auto_resolve", "snooze[0].sustained");
    }

    [NativeFact]
    public async Task A_snooze_evaluation_of_replaced_conditions_writes_no_sustained_timer()
    {
        var (tenant, rule, _) = await SeedAsync(active: false, clientConfiguration: SustainedSnooze);
        await using var db = Context(tenant);
        var edit = EditSnoozeOnAnotherReplica(tenant, rule.Id, ReorderedSnooze);

        await Engine(new EditBeforeLockRepository(db, edit))
            .EvaluateSnoozeAsync(rule.Id, SnoozeConditions, Glucose(120), CancellationToken.None);

        (await TimersAsync(tenant)).Should().BeEmpty();
    }

    [NativeFact]
    public async Task A_snooze_evaluation_of_replaced_conditions_does_not_extend()
    {
        var (tenant, rule, _) = await SeedAsync(active: false, clientConfiguration: SustainedSnooze);
        var started = Now.UtcDateTime.AddMinutes(-10);
        await using (var seed = Context(tenant))
        {
            seed.AlertConditionTimers.Add(new AlertConditionTimerEntity
            {
                TenantId = tenant,
                AlertRuleId = rule.Id,
                ConditionPath = "snooze[0].sustained",
                FirstTrueAt = started,
            });
            await seed.SaveChangesAsync();
        }
        await using var db = Context(tenant);
        var edit = EditSnoozeOnAnotherReplica(tenant, rule.Id, LongerSustainedSnooze);

        var holds = await Engine(new EditBeforeLockRepository(db, edit))
            .EvaluateSnoozeAsync(rule.Id, SnoozeConditions, Glucose(120), CancellationToken.None);

        holds.Should().BeFalse();
        (await TimersAsync(tenant)).Should().Equal(new Dictionary<string, DateTime> { ["snooze[0].sustained"] = started });
    }

    [NativeFact]
    public async Task A_snooze_evaluation_of_the_current_conditions_writes_its_sustained_timer()
    {
        var (tenant, rule, _) = await SeedAsync(active: false, clientConfiguration: SustainedSnooze);
        await using var db = Context(tenant);

        var holds = await Engine(new EditBeforeLockRepository(db, () => Task.CompletedTask))
            .EvaluateSnoozeAsync(rule.Id, SnoozeConditions, Glucose(120), CancellationToken.None);

        holds.Should().BeFalse("the window starts on the first true tick");
        (await TimersAsync(tenant)).Should().Equal(new Dictionary<string, DateTime> { ["snooze[0].sustained"] = Now.UtcDateTime });
    }
}
