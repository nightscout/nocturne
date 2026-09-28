using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts;
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
/// nothing, and in particular sets no re-arm hold the edit cleared (docs/alerts/engine-semantics.md §6.3).
/// </summary>
[Trait("Category", "Integration")]
public class StaleRuleDecisionPostgresTests(StaleRuleDecisionPostgresTests.Database database)
    : IClassFixture<StaleRuleDecisionPostgresTests.Database>
{
    private const string Body = """{"direction":"below","value":70}""";
    private const string EditedBody = """{"direction":"below","value":60}""";
    private const string Resolve = """{"type":"threshold","threshold":{"direction":"below","value":90}}""";
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
        : AlertTrackerRepository(context)
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

    private async Task<(Guid Tenant, AlertRuleSnapshot Rule, Guid? ExcursionId)> SeedAsync(bool active)
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
                ConditionType = AlertConditionType.Threshold,
                ConditionParams = Body,
                ClientConfiguration = "{}",
                Severity = AlertRuleSeverity.Warning,
                IsEnabled = true,
                AutoResolveEnabled = true,
                AutoResolveParams = Resolve,
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
            rule, tenant, "Low", AlertConditionType.Threshold, Body, AlertRuleSeverity.Warning, "{}", 0,
            AutoResolveEnabled: true, AutoResolveParams: Resolve);
        return (tenant, snapshot, excursionId);
    }

    /// <summary>The conditions edit an alert rule editor saves, with its own lease, on another replica.</summary>
    private Func<Task> EditOnAnotherReplica(Guid tenant, Guid rule) => async () =>
    {
        await using var db = Context(tenant);
        var stored = await db.AlertRules.SingleAsync(r => r.Id == rule);
        stored.ConditionParams = EditedBody;
        stored.UpdatedAt = DateTime.UtcNow;
        await new AlertRuleRearm(new AlertRuleEvaluationGate(), new AlertTrackerRepository(db))
            .SaveAndClearAsync(db, rule, CancellationToken.None);
    };

    private static ExcursionTracker Tracker(AlertTrackerRepository repository) =>
        new(repository, new AlertRuleEvaluationGate(), new FakeTimeProvider(Now), NullLogger<ExcursionTracker>.Instance);

    private async Task<(string State, Guid? ActiveExcursionId, bool AwaitingRearm, int OpenExcursions)> StoredAsync(Guid tenant)
    {
        await using var db = Context(tenant);
        var state = await db.AlertTrackerState.AsNoTracking().SingleOrDefaultAsync();
        var open = await db.AlertExcursions.AsNoTracking().CountAsync(e => e.EndedAt == null);
        return (state?.State ?? "none", state?.ActiveExcursionId, state?.AwaitingRearm ?? false, open);
    }

    [Fact]
    public async Task A_pre_edit_auto_resolve_close_sets_no_hold()
    {
        var (tenant, rule, excursionId) = await SeedAsync(active: true);
        await using var db = Context(tenant);
        // The evaluating context already holds the rule row, as a scoped context can.
        await db.AlertRules.SingleAsync(r => r.Id == rule.Id);

        var transition = await Tracker(new EditBeforeLockRepository(db, EditOnAnotherReplica(tenant, rule.Id)))
            .ForceCloseAsync(rule, ExcursionCloseReason.AutoResolve, CancellationToken.None);

        transition.Type.Should().Be(ExcursionTransitionType.None);
        (await StoredAsync(tenant)).Should().Be(("active", excursionId, false, 1));
    }

    [Fact]
    public async Task A_pre_edit_evaluation_opens_nothing()
    {
        var (tenant, rule, _) = await SeedAsync(active: false);
        await using var db = Context(tenant);

        var transition = await Tracker(new EditBeforeLockRepository(db, EditOnAnotherReplica(tenant, rule.Id)))
            .ProcessEvaluationAsync(rule, conditionMet: true, null, CancellationToken.None);

        transition.Type.Should().Be(ExcursionTransitionType.None);
        (await StoredAsync(tenant)).Should().Be(("none", (Guid?)null, false, 0));
    }

    [Fact]
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

    [Fact]
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
}
