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
/// Runs <c>RedactSettingsValuesInAuditLog</c> over the audit rows of a tenant on a real PostgreSQL.
/// </summary>
public sealed class RedactSettingsValuesInAuditLogFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261024075043_AddAlertExcursionReceipts";

    internal static readonly Guid Tenant = Guid.Parse("a1111111-1111-7111-8111-111111112619");
    internal static readonly Guid SecondTenant = Guid.Parse("b2222222-2222-7222-8222-222222222619");

    internal static readonly Guid SettingsUpdate = Guid.CreateVersion7();
    internal static readonly Guid SettingsDelete = Guid.CreateVersion7();
    internal static readonly Guid SettingsTimestampOnly = Guid.CreateVersion7();
    internal static readonly Guid OtherEntity = Guid.CreateVersion7();
    internal static readonly Guid SecondTenantRestore = Guid.CreateVersion7();

    internal const string SettingsUpdateChanges =
        """{"Value": {"old": "{\"apiToken\":\"a-secret\"}", "new": "{}"}, "SrvModified": {"old": "2026-01-01", "new": "2026-01-02"}}""";
    internal const string SettingsDeleteChanges =
        """{"Key": "aaps", "Value": "x", "AdditionalPropertiesJson": "{\"pairing\":\"p-secret\"}"}""";
    internal const string SettingsTimestampOnlyChanges = """{"SrvModified": {"old": "2026-01-01", "new": "2026-01-02"}}""";
    internal const string SecondTenantRestoreChanges =
        """{"Key": "aaps", "Value": "{\"apiToken\":\"b-secret\"}", "AdditionalPropertiesJson": null}""";
    internal const string OtherEntityChanges = """{"Value": {"old": 1, "new": 2}}""";

    internal Dictionary<Guid, JsonNode?> After { get; } = [];

    private string _migrator = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("redact_settings_audit");
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
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{Tenant}', 'tenant-a', 'Tenant A', true, now(), now()),
                   ('{SecondTenant}', 'tenant-b', 'Tenant B', true, now(), now());
            SELECT set_config('app.current_tenant_id', '{Tenant}', false);
            INSERT INTO mutation_audit_log (id, tenant_id, entity_type, entity_id, action, changes, created_at) VALUES
              ('{SettingsUpdate}', '{Tenant}', 'Settings', '{Guid.CreateVersion7()}', 'update', '{SettingsUpdateChanges}'::jsonb, now()),
              ('{SettingsDelete}', '{Tenant}', 'Settings', '{Guid.CreateVersion7()}', 'delete', '{SettingsDeleteChanges}'::jsonb, now()),
              ('{SettingsTimestampOnly}', '{Tenant}', 'Settings', '{Guid.CreateVersion7()}', 'update', '{SettingsTimestampOnlyChanges}'::jsonb, now()),
              ('{OtherEntity}', '{Tenant}', 'Food', '{Guid.CreateVersion7()}', 'update', '{OtherEntityChanges}'::jsonb, now());
            SELECT set_config('app.current_tenant_id', '{SecondTenant}', false);
            INSERT INTO mutation_audit_log (id, tenant_id, entity_type, entity_id, action, changes, created_at) VALUES
              ('{SecondTenantRestore}', '{SecondTenant}', 'Settings', '{Guid.CreateVersion7()}', 'restore', '{SecondTenantRestoreChanges}'::jsonb, now());
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
        foreach (var tenant in new[] { Tenant, SecondTenant })
        {
            await Execute(conn, $"SELECT set_config('app.current_tenant_id', '{tenant}', false);");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, changes::text FROM mutation_audit_log;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                After[reader.GetGuid(0)] = JsonNode.Parse(reader.GetString(1));
        }
    }
}

[Trait("Category", "Integration")]
public class RedactSettingsValuesInAuditLogTests : IClassFixture<RedactSettingsValuesInAuditLogFixture>
{
    private readonly RedactSettingsValuesInAuditLogFixture _fixture;

    public RedactSettingsValuesInAuditLogTests(RedactSettingsValuesInAuditLogFixture fixture) => _fixture = fixture;

    [Fact]
    public void A_settings_update_keeps_that_the_value_changed_but_not_what_it_held()
    {
        var changes = _fixture.After[RedactSettingsValuesInAuditLogFixture.SettingsUpdate]!;
        changes["Value"]!["old"]!.GetValue<string>().Should().Be("[redacted]");
        changes["Value"]!["new"]!.GetValue<string>().Should().Be("[redacted]");
        changes["SrvModified"]!["new"]!.GetValue<string>().Should().Be("2026-01-02");
    }

    [Fact]
    public void A_settings_delete_snapshot_keeps_its_key_but_not_its_values()
    {
        var changes = _fixture.After[RedactSettingsValuesInAuditLogFixture.SettingsDelete]!;
        changes["Key"]!.GetValue<string>().Should().Be("aaps");
        changes["Value"]!.GetValue<string>().Should().Be("[redacted]");
        changes["AdditionalPropertiesJson"]!.GetValue<string>().Should().Be("[redacted]");
    }

    [Fact]
    public void Every_tenant_is_scrubbed()
    {
        var changes = _fixture.After[RedactSettingsValuesInAuditLogFixture.SecondTenantRestore]!;
        changes["Value"]!.GetValue<string>().Should().Be("[redacted]");
        changes["AdditionalPropertiesJson"]!.GetValue<string>().Should().Be("[redacted]");
    }

    [Fact]
    public void Other_rows_are_untouched()
    {
        JsonNode.DeepEquals(
                _fixture.After[RedactSettingsValuesInAuditLogFixture.SettingsTimestampOnly],
                JsonNode.Parse(RedactSettingsValuesInAuditLogFixture.SettingsTimestampOnlyChanges))
            .Should().BeTrue();
        JsonNode.DeepEquals(
                _fixture.After[RedactSettingsValuesInAuditLogFixture.OtherEntity],
                JsonNode.Parse(RedactSettingsValuesInAuditLogFixture.OtherEntityChanges))
            .Should().BeTrue();
    }
}
