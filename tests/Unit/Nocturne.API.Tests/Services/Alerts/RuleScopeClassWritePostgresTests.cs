using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The backfill's write reaches a rule only while it still holds the body the job classified, so a
/// rule edited since keeps the class the editor's save stored. Run on PostgreSQL, where the body is
/// compared as <c>jsonb</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RuleScopeClassWritePostgresTests(RuleScopeClassWritePostgresTests.Database database)
    : IClassFixture<RuleScopeClassWritePostgresTests.Database>
{
    public sealed class Database() : MigratedDatabase("rule_scope_class_write");

    private const string Above250 = """{"schema_version":2,"operator":">","value":250}""";
    private const string Below70 = """{"schema_version":2,"operator":"<","value":70}""";

    private sealed record Fixture(Guid Tenant, AlertRepository Alerts, AlertRuleRepository Rules);

    private async Task<Fixture> CreateAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await conn.SeedTenantAsync(tenant, $"scope-class-{tenant:N}", "scope-class-write");
        }

        var contexts = new SuppliedContextFactory(() => TestContexts.Context(database.AppConnectionString, tenant));
        return new Fixture(tenant, new AlertRepository(contexts), new AlertRuleRepository(contexts, TimeProvider.System));
    }

    private static AlertRuleWrite Write(string body, RuleScopeClass scopeClass) => new(
        "Rule", null, AlertConditionType.Threshold, body, scopeClass, AlertRuleSeverity.Warning,
        IsEnabled: true, SortOrder: 1, AllowThroughDnd: false, AutoResolveEnabled: false,
        AutoResolveParams: null, ClientConfiguration: "{}");

    private async Task<RuleScopeClass> StoredClassAsync(Fixture fixture, Guid rule)
    {
        await using var db = TestContexts.Context(database.AppConnectionString, fixture.Tenant);
        return await db.AlertRules.AsNoTracking().Where(r => r.Id == rule).Select(r => r.ScopeClass).SingleAsync();
    }

    private static Task<int> BackfillAsync(Fixture fixture, RuleScopeClass scopeClass, AlertRuleSnapshot snapshot) =>
        fixture.Rules.SetScopeClassAsync(
            fixture.Tenant, scopeClass, [new ClassifiedRule(snapshot.Id, snapshot.ConditionType, snapshot.ConditionParams)],
            CancellationToken.None);

    [Fact]
    public async Task A_rule_edited_after_the_job_read_it_keeps_the_class_the_editor_stored()
    {
        var fixture = await CreateAsync();
        var rule = await fixture.Rules.CreateAsync(
            new NewAlertRule(Write(Above250, RuleScopeClass.Undirected), []), CancellationToken.None);
        var read = (await fixture.Alerts.GetRuleSnapshotsAsync(fixture.Tenant, CancellationToken.None)).Single();

        await fixture.Rules.UpdateAsync(rule, Write(Below70, RuleScopeClass.Low), CancellationToken.None);
        var written = await BackfillAsync(fixture, RuleScopeClass.High, read);

        written.Should().Be(0);
        (await StoredClassAsync(fixture, rule)).Should().Be(RuleScopeClass.Low);
    }

    [Fact]
    public async Task A_rule_unchanged_since_the_job_read_it_is_reclassified()
    {
        var fixture = await CreateAsync();
        var rule = await fixture.Rules.CreateAsync(
            new NewAlertRule(Write(Above250, RuleScopeClass.Undirected), []), CancellationToken.None);
        var read = (await fixture.Alerts.GetRuleSnapshotsAsync(fixture.Tenant, CancellationToken.None)).Single();

        var written = await BackfillAsync(fixture, RuleScopeClass.High, read);

        written.Should().Be(1);
        (await StoredClassAsync(fixture, rule)).Should().Be(RuleScopeClass.High);
    }

    [Fact]
    public async Task A_body_that_differs_only_in_spacing_and_key_order_is_the_same_body()
    {
        var fixture = await CreateAsync();
        var rule = await fixture.Rules.CreateAsync(
            new NewAlertRule(Write(Above250, RuleScopeClass.Undirected), []), CancellationToken.None);

        var written = await fixture.Rules.SetScopeClassAsync(
            fixture.Tenant, RuleScopeClass.High,
            [new ClassifiedRule(rule, AlertConditionType.Threshold, """{ "value": 250, "operator": ">", "schema_version": 2 }""")],
            CancellationToken.None);

        written.Should().Be(1);
        (await StoredClassAsync(fixture, rule)).Should().Be(RuleScopeClass.High);
    }

    [Fact]
    public async Task A_rule_whose_condition_type_changed_since_the_job_read_it_is_left_alone()
    {
        var fixture = await CreateAsync();
        var rule = await fixture.Rules.CreateAsync(
            new NewAlertRule(Write(Above250, RuleScopeClass.Undirected), []), CancellationToken.None);
        var read = (await fixture.Alerts.GetRuleSnapshotsAsync(fixture.Tenant, CancellationToken.None)).Single();

        var written = await fixture.Rules.SetScopeClassAsync(
            fixture.Tenant, RuleScopeClass.High,
            [new ClassifiedRule(rule, AlertConditionType.RateOfChange, read.ConditionParams)],
            CancellationToken.None);

        written.Should().Be(0);
        (await StoredClassAsync(fixture, rule)).Should().Be(RuleScopeClass.Undirected);
    }
}
