using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Storage.Internal;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// Tenant-scoping regression tests for <see cref="AlertRepository"/>. The repository leases
/// contexts from the pooled factory, which does not reset <c>TenantId</c>. These tests assert
/// (a) per-tenant methods pin the requested tenant even when the pooled context arrives tagged
/// with a different tenant, and (b) the cross-tenant sweeps iterate every active tenant rather
/// than relying on a single (RLS-blocked) query.
///
/// EF InMemory honours the dynamic <c>e.TenantId == context.TenantId</c> global filter, so it
/// validates the tenant pinning. InMemory has no Row-Level Security; the RLS half of the fix
/// follows the same <c>context.TenantId</c> pattern already proven by
/// <c>GetTenantAlertContextAsync</c> in production and is covered by Postgres integration tests.
/// </summary>
public class AlertRepositoryTenantScopingTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task GetEnabledRulesAsync_ScopesToRequestedTenant_EvenWhenPooledContextIsStale()
    {
        var options = NewStore();
        await SeedAsync(options, ctx =>
        {
            ctx.Tenants.AddRange(NewTenant(TenantA), NewTenant(TenantB));
            ctx.AlertRules.AddRange(
                NewRule(TenantA, "Overnight Low"),
                NewRule(TenantB, "High"),
                NewRule(TenantB, "Low"));
        });

        // The factory hands back contexts already tagged with tenant B — the production hazard
        // where a pooled context carries the previous lessee's tenant.
        var repo = new AlertRepository(new InMemoryContextFactory(options, staleTenantId: TenantB));

        var rules = await repo.GetEnabledRulesAsync(TenantA, CancellationToken.None);

        rules.Select(r => r.Name).Should().BeEquivalentTo(["Overnight Low"]);
    }

    [Fact]
    public async Task GetAllEnabledRulesAsync_ReturnsRulesAcrossEveryActiveTenant()
    {
        var options = NewStore();
        await SeedAsync(options, ctx =>
        {
            ctx.Tenants.AddRange(NewTenant(TenantA), NewTenant(TenantB));
            ctx.AlertRules.AddRange(
                NewRule(TenantA, "Signal loss A", AlertConditionType.SignalLoss),
                NewRule(TenantB, "Signal loss B", AlertConditionType.SignalLoss),
                NewRule(TenantA, "Threshold A"));
        });

        // Starts from an unset (Guid.Empty) context — the cross-tenant sweep must enumerate
        // tenants itself rather than depending on whatever tenant the pool last left behind.
        var repo = new AlertRepository(new InMemoryContextFactory(options, staleTenantId: Guid.Empty));

        var rules = await repo.GetAllEnabledRulesAsync(CancellationToken.None);

        rules.Select(r => (r.TenantId, r.Name)).Should().BeEquivalentTo(
            [(TenantA, "Signal loss A"), (TenantA, "Threshold A"), (TenantB, "Signal loss B")]);
    }

    [Fact]
    public async Task GetExcursionsInHysteresisAsync_SelectsByTrackerState()
    {
        var options = NewStore();
        var legacy = NewRule(TenantA, "Legacy hysteresis");
        var resumed = NewRule(TenantB, "Resumed");
        var legacyExcursion = Guid.NewGuid();
        var resumedExcursion = Guid.NewGuid();
        var start = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(options, ctx =>
        {
            ctx.Tenants.AddRange(NewTenant(TenantA), NewTenant(TenantB));
            ctx.AlertRules.AddRange(legacy, resumed);
            ctx.AlertExcursions.AddRange(
                new AlertExcursionEntity { Id = legacyExcursion, TenantId = TenantA, AlertRuleId = legacy.Id, StartedAt = start },
                new AlertExcursionEntity
                {
                    Id = resumedExcursion, TenantId = TenantB, AlertRuleId = resumed.Id, StartedAt = start,
                    HysteresisStartedAt = start.AddMinutes(5),
                });
            ctx.AlertTrackerState.AddRange(
                new AlertTrackerStateEntity
                {
                    AlertRuleId = legacy.Id, TenantId = TenantA, State = "hysteresis",
                    ActiveExcursionId = legacyExcursion, UpdatedAt = start.AddMinutes(5),
                },
                new AlertTrackerStateEntity
                {
                    AlertRuleId = resumed.Id, TenantId = TenantB, State = "active",
                    ActiveExcursionId = resumedExcursion, UpdatedAt = start.AddMinutes(6),
                });
        });
        var repo = new AlertRepository(new InMemoryContextFactory(options, staleTenantId: Guid.Empty));

        var inHysteresis = await repo.GetExcursionsInHysteresisAsync(CancellationToken.None);

        inHysteresis.Should().BeEquivalentTo([new HysteresisExcursionSnapshot(legacyExcursion, TenantA, legacy.Id, null)]);
    }

    [Fact]
    public async Task DeleteExcursionMutesAsync_DeletesEveryMuteOfTheClosedExcursionAndNoOther()
    {
        // SQLite rather than InMemory: the delete is a set-based ExecuteDelete.
        using var store = TestDbContextFactory.CreateSqliteWithTenant(TenantA, "a");
        var rule = NewRule(TenantA, "Low");
        var closed = Guid.NewGuid();
        var stillOpen = Guid.NewGuid();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await using (var seed = store.CreateContext())
        {
            seed.Subjects.AddRange(
                new SubjectEntity { Id = alice, Name = "Alice" },
                new SubjectEntity { Id = bob, Name = "Bob" });
            seed.AlertRules.Add(rule);
            seed.AlertExcursions.AddRange(
                new AlertExcursionEntity { Id = closed, TenantId = TenantA, AlertRuleId = rule.Id, StartedAt = DateTime.UtcNow },
                new AlertExcursionEntity { Id = stillOpen, TenantId = TenantA, AlertRuleId = rule.Id, StartedAt = DateTime.UtcNow });
            seed.AlertExcursionMutes.AddRange(
                Mute(alice, closed), Mute(bob, closed), Mute(alice, stillOpen));
            await seed.SaveChangesAsync();
        }

        await new AlertRepository(store.ContextFactory)
            .DeleteExcursionMutesAsync(TenantA, closed, CancellationToken.None);

        await using var read = store.CreateContext();
        (await read.AlertExcursionMutes.Select(m => new { m.SubjectId, m.AlertExcursionId }).ToListAsync())
            .Should().BeEquivalentTo([new { SubjectId = alice, AlertExcursionId = stillOpen }]);

        static AlertExcursionMuteEntity Mute(Guid subjectId, Guid excursionId) => new()
        {
            Id = Guid.NewGuid(), TenantId = TenantA, SubjectId = subjectId, AlertExcursionId = excursionId,
        };
    }

    private static DbContextOptions<NocturneDbContext> NewStore() =>
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase($"alert_repo_tests_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static async Task SeedAsync(DbContextOptions<NocturneDbContext> options, Action<NocturneDbContext> seed)
    {
        await using var ctx = new NocturneDbContext(options);
        seed(ctx);
        await ctx.SaveChangesAsync();
    }

    private static TenantEntity NewTenant(Guid id) => new()
    {
        Id = id,
        Slug = id.ToString("N")[..8],
        DisplayName = id.ToString("N")[..8],
        IsActive = true,
    };

    private static AlertRuleEntity NewRule(
        Guid tenantId, string name, AlertConditionType type = AlertConditionType.Threshold) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Name = name,
        ConditionType = type,
        ConditionParams = "{}",
        ClientConfiguration = "{}",
        Severity = AlertRuleSeverity.Warning,
        IsEnabled = true,
        SortOrder = 0,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    /// <summary>
    /// An <see cref="IDbContextFactory{TContext}"/> over a shared InMemory store whose leased
    /// contexts arrive pre-tagged with <paramref name="staleTenantId"/>, mimicking a pooled
    /// context that was not reset. The repository must overwrite <c>TenantId</c> itself.
    /// </summary>
    private sealed class InMemoryContextFactory(
        DbContextOptions<NocturneDbContext> options, Guid staleTenantId)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options) { TenantId = staleTenantId };

        public Task<NocturneDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
