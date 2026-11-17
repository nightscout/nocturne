using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Monitoring;

/// <summary>
/// Exercises <see cref="AlertRepository"/> against the real PostgreSQL instance under Row-Level
/// Security (the runtime <c>nocturne_app</c> role is <c>NOBYPASSRLS</c>). This is the half the
/// InMemory unit tests cannot cover: a method without a tenant must read the scope's tenant and
/// nothing of another's, and one that takes a tenant must read that tenant from a scope that has none.
///
/// The repository is constructed over a context factory wired with the same
/// <see cref="TenantConnectionInterceptor"/> the app uses, so <c>context.TenantId</c> drives the
/// <c>app.current_tenant_id</c> session variable exactly as in production.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class AlertRepositorySweepIntegrationTests : ApiIntegrationTestBase
{
    public AlertRepositorySweepIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task PerTenantReads_AreCorrectlyTenantScoped_UnderRls()
    {
        // Two fresh tenants (unique slugs so re-runs against a persisted container don't collide).
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenantA = await AuthTestHelpers.SeedTenantAsync(Fixture, $"sweep-a-{suffix}", "Sweep A");
        var tenantB = await AuthTestHelpers.SeedTenantAsync(Fixture, $"sweep-b-{suffix}", "Sweep B");

        // A context factory wired exactly like the app: Npgsql + the tenant RLS interceptor.
        // As the runtime role: the bootstrap superuser bypasses the policies under test, and the
        // interceptor refuses it.
        await using var dataSource = new NpgsqlDataSourceBuilder(Fixture.AppConnectionString).Build();
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

        var noTenant = new AlertRepository(factory.AsNocturneFactory());

        var rulesA = await new AlertRepository(factory.AsNocturneFactory(tenantA))
            .GetEnabledRulesAsync(CancellationToken.None);
        rulesA.Select(r => r.Name).Should().BeEquivalentTo(["Signal loss A", "High A"]);
        rulesA.Should().OnlyContain(r => r.TenantId == tenantA);

        var rulesB = await new AlertRepository(factory.AsNocturneFactory(tenantB))
            .GetEnabledRulesAsync(CancellationToken.None);
        rulesB.Select(r => r.Name).Should().BeEquivalentTo(["Signal loss B"]);
        rulesB.Should().OnlyContain(r => r.TenantId == tenantB);

        (await noTenant.GetEnabledRulesAsync(CancellationToken.None))
            .Should().BeEmpty("a scope with no tenant reads no tenant's rules");
    }

    [Fact]
    public async Task A_rule_whose_condition_type_or_severity_no_member_has_is_skipped_and_logged_not_the_tenants_other_rules()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var stale = await AuthTestHelpers.SeedTenantAsync(Fixture, $"stale-{suffix}", "Stale");
        var other = await AuthTestHelpers.SeedTenantAsync(Fixture, $"other-{suffix}", "Other");

        await using var dataSource = new NpgsqlDataSourceBuilder(Fixture.AppConnectionString).Build();
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(dataSource)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;
        var factory = new InterceptingContextFactory(options);

        await SeedRuleAsync(factory, stale, "Readable", AlertConditionType.Threshold);
        await SeedRuleAsync(factory, other, "Other tenant", AlertConditionType.Threshold);
        var legacyType = await SeedRawRuleAsync(dataSource, stale, "threshold_low", "warning");
        var legacySeverity = await SeedRawRuleAsync(dataSource, stale, "threshold", "normal");

        var logger = new ListLogger<AlertRepository>();
        var repo = new AlertRepository(factory.AsNocturneFactory(), logger);

        var rules = await new AlertRepository(factory.AsNocturneFactory(stale), logger)
            .GetEnabledRulesAsync(CancellationToken.None);
        rules.Select(r => r.Name).Should().Equal("Readable");

        var read = await repo.ReadEnabledRulesAsync(stale, CancellationToken.None);
        read.Rules.Select(r => r.Name).Should().Equal("Readable");
        read.UnreadableRuleIds.Should().BeEquivalentTo([legacyType, legacySeverity]);

        logger.Errors.Should().Contain(m => m.Contains(legacyType.ToString()) && m.Contains("threshold_low"));
        logger.Errors.Should().Contain(m => m.Contains(legacySeverity.ToString()) && m.Contains("normal"));
    }

    private static async Task<Guid> SeedRawRuleAsync(
        NpgsqlDataSource dataSource, Guid tenantId, string conditionType, string severity)
    {
        var id = Guid.CreateVersion7();
        await using var cmd = dataSource.CreateCommand($"""
            SELECT set_config('app.current_tenant_id', '{tenantId}', false);
            INSERT INTO alert_rules (id, tenant_id, name, condition_type, condition_params, auto_resolve_enabled,
                                     severity, client_configuration, is_enabled, allow_through_dnd, scope_class,
                                     sort_order, created_at, updated_at)
            VALUES ('{id}', '{tenantId}', 'Stale', '{conditionType}', '{"{}"}', false,
                    '{severity}', '{"{}"}', true, false, 'undirected', 1, now(), now());
            """);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Error)
                Errors.Add(formatter(state, exception));
        }
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
