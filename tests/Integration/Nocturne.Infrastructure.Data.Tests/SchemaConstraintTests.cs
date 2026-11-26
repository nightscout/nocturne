using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>A migrated database with one tenant holding one connector, stored under its canonical name.</summary>
public class SchemaConstraintFixture() : MigratedDatabase("schema_constraints")
{
    internal static readonly Guid Tenant = Guid.Parse("11111111-1111-7111-8111-111111111111");

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var conn = await OpenPinnedAsync();
        await conn.SeedTenantAsync(Tenant, "tenant", "Tenant");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO connector_configurations
              (id, tenant_id, connector_name, configuration, secrets, schema_version, last_modified, sys_created_at, sys_updated_at, is_healthy)
            VALUES
              (gen_random_uuid(), '{{Tenant}}', 'dexcom', '{}'::jsonb, '{}'::jsonb, 1, now(), now(), now(), true);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Runs <paramref name="sql"/> for <see cref="Tenant"/> in a transaction it rolls back, and
    /// returns the error it raised, if any.
    /// </summary>
    internal async Task<PostgresException?> AttemptAsync(string sql)
    {
        await using var conn = await OpenPinnedAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        try
        {
            await cmd.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex;
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private async Task<NpgsqlConnection> OpenPinnedAsync()
    {
        var conn = new NpgsqlConnection(MigratorConnectionString);
        await conn.OpenAsync();
        await conn.PinTenantAsync(Tenant);
        return conn;
    }
}

[Trait("Category", "Integration")]
public class SchemaConstraintTests(SchemaConstraintFixture fixture) : IClassFixture<SchemaConstraintFixture>
{
    private const string LowercaseConnectorName = "ck_connector_configurations_connector_name_lower";

    /// <summary>
    /// Connectors are looked up by their lowercase route name, so a row stored under another
    /// spelling is one no lookup finds; the constraint makes the write fail instead.
    /// </summary>
    [Fact]
    public async Task A_connector_stored_under_another_spelling_is_rejected()
    {
        var error = await fixture.AttemptAsync(
            $$"""
            INSERT INTO connector_configurations
              (id, tenant_id, connector_name, configuration, secrets, schema_version, last_modified, sys_created_at, sys_updated_at, is_healthy)
            VALUES
              (gen_random_uuid(), '{{SchemaConstraintFixture.Tenant}}', 'CareLink', '{}'::jsonb, '{}'::jsonb, 1, now(), now(), now(), true);
            """);

        error.Should().NotBeNull();
        error!.ConstraintName.Should().Be(LowercaseConnectorName);
    }

    [Fact]
    public async Task A_connector_renamed_to_another_spelling_is_rejected()
    {
        var error = await fixture.AttemptAsync(
            "UPDATE connector_configurations SET connector_name = 'Dexcom';");

        error.Should().NotBeNull();
        error!.ConstraintName.Should().Be(LowercaseConnectorName);
    }

    /// <summary>Each registration of an app is a client of its own, so its software id repeats.</summary>
    [Fact]
    public async Task Two_clients_may_share_a_software_id()
    {
        var error = await fixture.AttemptAsync(
            $"""
            INSERT INTO oauth_clients (id, tenant_id, client_id, software_id, is_known, redirect_uris)
            VALUES (gen_random_uuid(), '{SchemaConstraintFixture.Tenant}', gen_random_uuid()::text, 'com.nocturne.tray', true, '[]'),
                   (gen_random_uuid(), '{SchemaConstraintFixture.Tenant}', gen_random_uuid()::text, 'com.nocturne.tray', true, '[]');
            """);

        error.Should().BeNull();
    }
}
