using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Integration.Migration;

/// <summary>
/// Runs <c>RetireUiSettingsAggregate</c> over three tenants on a real PostgreSQL: one whose settings
/// live only in <c>ui:settings:complete</c>, one that already owns some section rows, and one whose
/// aggregate is not JSON.
/// </summary>
public sealed class RetireUiSettingsAggregateFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261024075410_PartialSparseForeignKeyIndexes";

    internal static readonly Guid AggregateOnly = Guid.Parse("a1111111-1111-7111-8111-111111112605");
    internal static readonly Guid PartlyOwned = Guid.Parse("b2222222-2222-7222-8222-222222222605");
    internal static readonly Guid Corrupt = Guid.Parse("c3333333-3333-7333-8333-333333332605");
    internal static readonly Guid NulEscape = Guid.Parse("d4444444-4444-7444-8444-444444442605");

    private const string Aggregate = """
        {"devices": {"autoConnect": false},
         "algorithm": {"prediction": {"minutes": 45}},
         "features": {"display": {"units": "mmol/L"}},
         "notifications": {"alarmsEnabled": true, "alarmConfiguration": {"profiles": [{"id": "low", "threshold": 88}]}},
         "services": {"syncSettings": {"autoSync": false}, "availableServices": [{"id": "dexcom"}]},
         "dataQuality": {"sleepSchedule": {"bedtimeHour": 1}}}
        """;

    internal Dictionary<(Guid Tenant, string Key), (JsonNode? Value, bool IsActive)> After { get; } = [];

    private string _migrator = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("retire_ui_settings_aggregate");
        _migrator = database.MigratorConnectionString;

        await MigrateToAsync(PriorMigration);
        await SeedAsync();
        await MigrateToAsync(null);
        await ReadAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string? target)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migrator, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task SeedAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await Execute(conn, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{AggregateOnly}', 'aggregate-only', 'Aggregate only', true, now(), now()),
              ('{PartlyOwned}', 'partly-owned', 'Partly owned', true, now(), now()),
              ('{Corrupt}', 'corrupt', 'Corrupt', true, now(), now()),
              ('{NulEscape}', 'nul-escape', 'Nul escape', true, now(), now());
            """);

        await Seed(conn, AggregateOnly, ("ui:settings:complete", Aggregate, true));
        await Seed(conn, PartlyOwned,
            ("ui:settings:complete", Aggregate, true),
            ("ui:settings:features", """{"display": {"units": "mg/dL"}}""", true),
            ("ui:settings:devices", """{"autoConnect": true}""", false),
            ("ui:settings:algorithm", "null", true),
            ("ui:settings:notifications", """{"alarmsEnabled": false}""", true),
            ("ui:settings:dataquality", "\tnull\n", true));
        await Seed(conn, Corrupt, ("ui:settings:complete", "not json", true));
        await Seed(conn, NulEscape,
            ("ui:settings:complete", """{"devices": {"autoConnect": false, "name": "a\u0000b"}}""", true));
    }

    private static async Task Seed(
        NpgsqlConnection conn, Guid tenant, params (string Key, string Value, bool IsActive)[] rows)
    {
        var values = string.Join(",\n", rows.Select(r =>
            $"('{Guid.CreateVersion7()}', '{tenant}', '{r.Key}', '{r.Value}', 0, {(r.IsActive ? "true" : "false")})"));
        await Execute(conn, $"""
            SELECT set_config('app.current_tenant_id', '{tenant}', false);
            INSERT INTO settings (id, tenant_id, key, value, mills, is_active) VALUES
            {values};
            SELECT set_config('app.current_tenant_id', '', false);
            """);
    }

    private static async Task Execute(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ReadAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        foreach (var tenant in new[] { AggregateOnly, PartlyOwned, Corrupt, NulEscape })
        {
            await Execute(conn, $"SELECT set_config('app.current_tenant_id', '{tenant}', false);");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value, is_active FROM settings;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var value = reader.IsDBNull(1) ? null : JsonNode.Parse(reader.GetString(1));
                After[(tenant, reader.GetString(0))] = (value, reader.GetBoolean(2));
            }
        }
    }
}

[Trait("Category", "Integration")]
public class RetireUiSettingsAggregateTests : IClassFixture<RetireUiSettingsAggregateFixture>
{
    private readonly RetireUiSettingsAggregateFixture _fixture;

    public RetireUiSettingsAggregateTests(RetireUiSettingsAggregateFixture fixture) => _fixture = fixture;

    private JsonNode Row(Guid tenant, string key)
    {
        _fixture.After.Should().ContainKey((tenant, key));
        var (value, isActive) = _fixture.After[(tenant, key)];
        isActive.Should().BeTrue();
        return value!;
    }

    [Fact]
    public void No_tenant_keeps_an_aggregate_row()
    {
        _fixture.After.Keys.Should().NotContain(k => k.Key == "ui:settings:complete");
    }

    [Fact]
    public void Every_section_of_an_aggregate_only_tenant_gets_its_own_row()
    {
        var tenant = RetireUiSettingsAggregateFixture.AggregateOnly;
        Row(tenant, "ui:settings:devices")["autoConnect"]!.GetValue<bool>().Should().BeFalse();
        Row(tenant, "ui:settings:algorithm")["prediction"]!["minutes"]!.GetValue<int>().Should().Be(45);
        Row(tenant, "ui:settings:features")["display"]!["units"]!.GetValue<string>().Should().Be("mmol/L");
        Row(tenant, "ui:settings:dataquality")["sleepSchedule"]!["bedtimeHour"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void The_alarm_configuration_moves_to_its_own_row_and_off_the_notifications_row()
    {
        var tenant = RetireUiSettingsAggregateFixture.AggregateOnly;
        var notifications = Row(tenant, "ui:settings:notifications").AsObject();
        notifications["alarmsEnabled"]!.GetValue<bool>().Should().BeTrue();
        notifications.ContainsKey("alarmConfiguration").Should().BeFalse();
        Row(tenant, "ui:settings:notifications:alarms")["profiles"]![0]!["threshold"]!.GetValue<int>()
            .Should().Be(88);
    }

    [Fact]
    public void The_services_row_does_not_carry_the_connector_catalog()
    {
        var services = Row(RetireUiSettingsAggregateFixture.AggregateOnly, "ui:settings:services").AsObject();
        services["syncSettings"]!["autoSync"]!.GetValue<bool>().Should().BeFalse();
        services.ContainsKey("availableServices").Should().BeFalse();
    }

    [Fact]
    public void A_row_that_already_serves_a_section_wins_over_the_aggregate()
    {
        var tenant = RetireUiSettingsAggregateFixture.PartlyOwned;
        Row(tenant, "ui:settings:features")["display"]!["units"]!.GetValue<string>().Should().Be("mg/dL");
        Row(tenant, "ui:settings:notifications")["alarmsEnabled"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void An_owned_notifications_row_keeps_the_aggregate_alarm_configuration_out()
    {
        _fixture.After.Keys.Should().NotContain(
            (RetireUiSettingsAggregateFixture.PartlyOwned, "ui:settings:notifications:alarms"));
    }

    [Fact]
    public void A_deactivated_or_json_null_row_takes_the_aggregate_section()
    {
        var tenant = RetireUiSettingsAggregateFixture.PartlyOwned;
        Row(tenant, "ui:settings:devices")["autoConnect"]!.GetValue<bool>().Should().BeFalse();
        Row(tenant, "ui:settings:algorithm")["prediction"]!["minutes"]!.GetValue<int>().Should().Be(45);
    }

    [Fact]
    public void A_row_of_json_null_padded_with_whitespace_takes_the_aggregate_section()
    {
        Row(RetireUiSettingsAggregateFixture.PartlyOwned, "ui:settings:dataquality")["sleepSchedule"]!["bedtimeHour"]!
            .GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void A_nul_escape_jsonb_would_refuse_moves_as_a_replacement_character()
    {
        var devices = Row(RetireUiSettingsAggregateFixture.NulEscape, "ui:settings:devices");
        devices["autoConnect"]!.GetValue<bool>().Should().BeFalse();
        devices["name"]!.GetValue<string>().Should().Be("a�b");
    }

    [Fact]
    public void An_aggregate_that_is_not_json_is_dropped_without_moving_anything()
    {
        _fixture.After.Keys.Should().NotContain(k => k.Tenant == RetireUiSettingsAggregateFixture.Corrupt);
    }
}
