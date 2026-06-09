using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Monitoring;

/// <summary>
/// Exercises <see cref="AlertRepository"/> against the real PostgreSQL instance under Row-Level
/// Security (the runtime <c>nocturne_app</c> role is <c>NOBYPASSRLS</c>). This is the half the
/// InMemory unit tests cannot cover: the cross-tenant sweep methods must iterate active tenants
/// — a single query cannot see across tenants because the fail-closed RLS policy
/// (<c>tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid</c>) admits
/// no "see all" value — and the per-tenant methods must pin the tenant so RLS returns its rows.
///
/// The repository is constructed over a context factory wired with the same
/// <see cref="TenantConnectionInterceptor"/> the app uses, so <c>context.TenantId</c> drives the
/// <c>app.current_tenant_id</c> session variable exactly as in production.
/// </summary>
[Trait("Category", "Integration")]
public class AlertRepositorySweepIntegrationTests : AspireIntegrationTestBase
{
    public AlertRepositorySweepIntegrationTests(
        AspireIntegrationTestFixture fixture,
        ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task SweepAndPerTenantReads_AreCorrectlyTenantScoped_UnderRls()
    {
        var connStr = await GetPostgresConnectionStringAsync()
                      ?? throw new InvalidOperationException("No PostgreSQL connection string.");

        // Two fresh tenants (unique slugs so re-runs against a persisted container don't collide).
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenantA = await AuthTestHelpers.SeedTenantAsync(conn, $"sweep-a-{suffix}", "Sweep A");
        var tenantB = await AuthTestHelpers.SeedTenantAsync(conn, $"sweep-b-{suffix}", "Sweep B");

        // A context factory wired exactly like the app: Npgsql + the tenant RLS interceptor.
        await using var dataSource = new NpgsqlDataSourceBuilder(connStr).Build();
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(dataSource)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;
        var factory = new InterceptingContextFactory(options);

        // Seed rules through EF so the enum/jsonb mapping matches the live schema. The interceptor
        // sets the RLS GUC from context.TenantId, satisfying the WITH CHECK policy on insert.
        await SeedRuleAsync(factory, tenantA, "Signal loss A", AlertConditionType.SignalLoss);
        await SeedRuleAsync(factory, tenantA, "High A", AlertConditionType.Threshold);
        await SeedRuleAsync(factory, tenantB, "Signal loss B", AlertConditionType.SignalLoss);

        var repo = new AlertRepository(factory);

        // Cross-tenant sweep: must surface signal-loss rules for BOTH tenants.
        var signalLoss = await repo.GetEnabledSignalLossRulesAsync(CancellationToken.None);
        var signalLossTenants = signalLoss.Select(r => r.TenantId).ToHashSet();
        signalLossTenants.Should().Contain(tenantA);
        signalLossTenants.Should().Contain(tenantB);

        // Per-tenant read: tenant A sees only its own rules, never tenant B's.
        var rulesA = await repo.GetEnabledRulesAsync(tenantA, CancellationToken.None);
        rulesA.Select(r => r.Name).Should().BeEquivalentTo(["Signal loss A", "High A"]);
        rulesA.Should().OnlyContain(r => r.TenantId == tenantA);

        var rulesB = await repo.GetEnabledRulesAsync(tenantB, CancellationToken.None);
        rulesB.Select(r => r.Name).Should().BeEquivalentTo(["Signal loss B"]);
        rulesB.Should().OnlyContain(r => r.TenantId == tenantB);
    }

    private static async Task SeedRuleAsync(
        InterceptingContextFactory factory, Guid tenantId, string name, AlertConditionType type)
    {
        await using var ctx = factory.CreateDbContext();
        ctx.TenantId = tenantId; // pins the RLS GUC so the insert's WITH CHECK passes
        ctx.AlertRules.Add(new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = name,
            ConditionType = type,
            ConditionParams = type == AlertConditionType.SignalLoss ? "{\"timeoutMinutes\":20}" : "{}",
            ClientConfiguration = "{}",
            Severity = AlertRuleSeverity.Warning,
            IsEnabled = true,
            SortOrder = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private sealed class InterceptingContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }
}
