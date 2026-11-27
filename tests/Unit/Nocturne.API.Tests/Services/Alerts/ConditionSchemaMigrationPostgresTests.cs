using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Logging;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The startup migration of stored condition trees (<see cref="ConditionSchemaMigration"/>) on
/// PostgreSQL under the runtime role and Row Level Security: every tenant's rules, read and written
/// with that tenant pinned, through a context factory with no scope as the startup has.
/// </summary>
[Trait("Category", "Integration")]
public class ConditionSchemaMigrationPostgresTests(ConditionSchemaMigrationPostgresTests.Database database)
    : IClassFixture<ConditionSchemaMigrationPostgresTests.Database>
{
    private static readonly DateTime Stamp = new(2026, 11, 1, 9, 30, 0, DateTimeKind.Utc);

    public sealed class Database() : MigratedDatabase("condition_schema");

    private sealed record Row(
        string ConditionParams, string? AutoResolveParams, string ClientConfiguration,
        string? PreRepair, string? ReviewReason, DateTime UpdatedAt);

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await NpgsqlTestCommands.SeedTenantAsync(database.SuperuserConnectionString, tenant);
        return tenant;
    }

    private async Task<Guid> SeedRuleAsync(
        Guid tenant, string conditionType, string conditionParams, string? autoResolve = null,
        string clientConfiguration = "{}", string? preRepair = null, bool enabled = true)
    {
        var rule = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsTenantAsync(database.SuperuserConnectionString, tenant, """
            INSERT INTO alert_rules (id, tenant_id, name, condition_type, condition_params, auto_resolve_enabled,
                auto_resolve_params, client_configuration, pre_repair, severity, is_enabled, sort_order,
                created_at, updated_at)
            VALUES (@id, @tenant, 'rule', @type, @params::jsonb, @autoResolveEnabled, @autoResolve::jsonb,
                @clientConfiguration::jsonb, @preRepair::jsonb, 'warning', @enabled, 0, @stamp, @stamp)
            """,
            ("id", rule), ("tenant", tenant), ("type", conditionType), ("params", conditionParams),
            ("autoResolveEnabled", autoResolve is not null), ("autoResolve", (object?)autoResolve ?? DBNull.Value),
            ("clientConfiguration", clientConfiguration), ("preRepair", (object?)preRepair ?? DBNull.Value),
            ("enabled", enabled), ("stamp", Stamp));
        return rule;
    }

    private async Task<Row> ReadAsync(Guid rule)
    {
        await using var conn = new NpgsqlConnection(database.SuperuserConnectionString);
        await conn.OpenAsync();
        var rows = await conn.QueryAsync(
            """
            SELECT condition_params::text, auto_resolve_params::text, client_configuration::text,
                   pre_repair::text, review_reason, updated_at
            FROM alert_rules WHERE id = @id
            """,
            r => new Row(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.GetDateTime(5)),
            ("id", rule));
        return rows.Single();
    }

    private async Task<(ConditionSchemaMigration.Summary Summary, ListLogger Logger)> RunAsync()
    {
        await using var provider = TestContexts.BuildProvider(database.AppConnectionString);
        var logger = new ListLogger();
        var summary = await ConditionSchemaMigration.RunAsync(
            provider.GetRequiredService<ITenantRepository>(), provider.GetRequiredService<INocturneDbContextFactory>(),
            RustAlertEngine.Migrate, logger, CancellationToken.None);
        return (summary, logger);
    }

    private static void ShouldBe(string? actual, string expected) =>
        JsonNode.DeepEquals(JsonNode.Parse(actual!), JsonNode.Parse(expected))
            .Should().BeTrue($"{actual} should be {expected}");

    [NativeFact]
    public async Task Every_tenants_trees_move_to_schema_2_once()
    {
        var first = await SeedTenantAsync();
        var second = await SeedTenantAsync();

        var low = await SeedRuleAsync(first, "threshold", """{"direction":"below","value":70}""",
            autoResolve: """{"type":"threshold","threshold":{"direction":"above","value":80}}""",
            clientConfiguration: """
                {"snooze":{"smartSnooze":true,"smartSnoozeExtendMinutes":15,"conditions":[{"type":"rate_of_change","rate_of_change":{"direction":"rising","rate":2}},{"type":"trend","trend":{"bucket":"rising"}}]},"note":"kept"}
                """,
            preRepair: """
                {"condition_type":"threshold","condition_params":{"direction":"below","value":3.9},"auto_resolve_enabled":false,"auto_resolve_params":null,"severity":"urgent","is_enabled":true}
                """);
        var site = await SeedRuleAsync(second, "site_age", """{"operator":">=","value":72.5083}""", enabled: false);
        var broken = await SeedRuleAsync(second, "threshold", """{"direction":"sideways","value":70}""");
        var current = await SeedRuleAsync(second, "signal_loss", """{"schema_version":2,"timeout_minutes":20}""");

        var (summary, logger) = await RunAsync();

        summary.Rewritten.Should().BeGreaterThanOrEqualTo(2);
        summary.ForReview.Should().BeGreaterThanOrEqualTo(1);
        summary.TreesLeft.Should().BeGreaterThanOrEqualTo(1);

        var lowRow = await ReadAsync(low);
        ShouldBe(lowRow.ConditionParams, """{"schema_version":2,"operator":"<","value":70}""");
        ShouldBe(lowRow.AutoResolveParams, """{"schema_version":2,"type":"threshold","threshold":{"operator":">","value":80}}""");
        ShouldBe(lowRow.ClientConfiguration, """
            {"snooze":{"smartSnooze":true,"smartSnoozeExtendMinutes":15,"conditions":[{"schema_version":2,"type":"rate_of_change","rate_of_change":{"operator":">=","value":2}},{"schema_version":2,"type":"trend","trend":{"bucket":"rising"}}]},"note":"kept"}
            """);
        ShouldBe(lowRow.PreRepair, """
            {"condition_type":"threshold","condition_params":{"schema_version":2,"operator":"<","value":3.9},"auto_resolve_enabled":false,"auto_resolve_params":null,"severity":"urgent","is_enabled":true}
            """);
        lowRow.ReviewReason.Should().BeNull();
        lowRow.UpdatedAt.Should().Be(Stamp, "the migration changes no answer, so it is no edit of the rule");

        var siteRow = await ReadAsync(site);
        ShouldBe(siteRow.ConditionParams, """{"schema_version":2,"operator":">=","minutes":4350}""");
        siteRow.ReviewReason.Should().Be(AlertRuleReviewReasons.ConditionDurationRounded);

        ShouldBe((await ReadAsync(broken)).ConditionParams, """{"direction":"sideways","value":70}""");
        logger.WarningEvents.Should().Contain(w => (Guid)w.Properties["RuleId"]! == broken
            && (string)w.Properties["Reason"]! == "unknown_value");

        ShouldBe((await ReadAsync(current)).ConditionParams, """{"schema_version":2,"timeout_minutes":20}""");

        var stored = await Task.WhenAll(new[] { low, site, broken, current }.Select(ReadAsync));

        var (again, _) = await RunAsync();

        again.Rewritten.Should().Be(0);
        again.Raced.Should().Be(0);
        (await Task.WhenAll(new[] { low, site, broken, current }.Select(ReadAsync))).Should().Equal(stored);
    }

    [NativeFact]
    public async Task Instances_migrating_at_once_leave_the_rule_migrated()
    {
        var tenant = await SeedTenantAsync();
        var rule = await SeedRuleAsync(tenant, "staleness", """{"operator":">","value":15}""");

        var runs = await Task.WhenAll(RunAsync(), RunAsync());

        ShouldBe((await ReadAsync(rule)).ConditionParams, """{"schema_version":2,"operator":">","minutes":15}""");
        runs.Sum(r => r.Summary.Rewritten).Should().BeGreaterThanOrEqualTo(1);
        runs.Select(r => r.Summary).Should().OnlyContain(s => s.TenantsFailed == 0);
    }
}
