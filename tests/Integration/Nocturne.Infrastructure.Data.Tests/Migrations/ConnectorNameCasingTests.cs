using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>NormalizeConnectorNameCasing</c> over seeded rows on a real PostgreSQL.
/// </summary>
/// <remarks>
/// The normalisation is a <c>DO</c> block whose loop iterates zero tenants on an empty database,
/// and the constraint it adds is the only thing that stops the split recurring — neither is worth
/// anything untested against rows. The seed covers the three shapes that reach the statements:
/// a row already canonical, a row written under the other spelling, and one tenant holding both.
/// <para>
/// One run, one seed: the fixture costs a container plus the whole migration chain, and the tenants
/// below do not interact.
/// </para>
/// </remarks>
public class ConnectorNameCasingFixture : IAsyncLifetime
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string PriorMigration = "20260912065418_AddOAuthGrantTokenHashIndex";


    private string _migratorConnectionString = string.Empty;

    /// <summary>Already canonical, and the only connector this tenant has.</summary>
    internal static readonly Guid Canonical = Guid.Parse("11111111-1111-7111-8111-111111111111");

    /// <summary>One row, written under the registration's spelling rather than the route's.</summary>
    internal static readonly Guid MisCased = Guid.Parse("22222222-2222-7222-8222-222222222222");

    /// <summary>Both spellings of one connector, each holding half the configuration.</summary>
    internal static readonly Guid BothCasings = Guid.Parse("33333333-3333-7333-8333-333333333333");

    public async Task InitializeAsync()
    {
        // Unmigrated: the fixture walks the chain up to the migration under test itself.
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("connector_casing");
        _migratorConnectionString = database.MigratorConnectionString;

        await MigrateToAsync(PriorMigration);
        await SeedAsync();
        await MigrateToAsync(targetMigration: null);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string? targetMigration)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migratorConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;

        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> OpenPinnedAsync(Guid tenantId)
    {
        var conn = await OpenAsync();
        await using var pin = conn.CreateCommand();
        pin.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
        pin.Parameters.AddWithValue("tenant", tenantId.ToString());
        await pin.ExecuteNonQueryAsync();
        return conn;
    }

    internal async Task<T?> ScalarAsync<T>(Guid tenantId, string sql)
    {
        await using var conn = await OpenPinnedAsync(tenantId);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>Runs <paramref name="sql"/> for a tenant and returns the error it raised, if any.</summary>
    internal async Task<PostgresException?> AttemptAsync(Guid tenantId, string sql)
    {
        await using var conn = await OpenPinnedAsync(tenantId);
        await using var cmd = conn.CreateCommand();
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
    }

    private async Task SeedAsync()
    {
        await ExecuteAsync($$"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{{Canonical}}',   'canonical',   'Canonical',   true, now(), now()),
              ('{{MisCased}}',    'miscased',    'MisCased',    true, now(), now()),
              ('{{BothCasings}}', 'bothcasings', 'BothCasings', true, now(), now());

            SELECT set_config('app.current_tenant_id', '{{Canonical}}', false);
            INSERT INTO connector_configurations
              (id, tenant_id, connector_name, configuration, secrets, schema_version, last_modified, sys_created_at, sys_updated_at, is_healthy)
            VALUES
              ('aaaaaaaa-0000-7000-8000-000000000001', '{{Canonical}}', 'dexcom',
               '{"enabled":true}'::jsonb, '{"password":"p"}'::jsonb, 1, now(), now(), now(), true);

            SELECT set_config('app.current_tenant_id', '{{MisCased}}', false);
            INSERT INTO connector_configurations
              (id, tenant_id, connector_name, configuration, secrets, schema_version, last_modified, sys_created_at, sys_updated_at, is_healthy)
            VALUES
              ('aaaaaaaa-0000-7000-8000-000000000002', '{{MisCased}}', 'CareLink',
               '{"server":"EU"}'::jsonb, '{"refresh_token":"r"}'::jsonb, 1, now(), now(), now(), true);

            -- The winner is the row that last synced successfully; the loser holds the only copy of
            -- two keys, which a pick-one-and-drop-the-rest collapse would lose.
            SELECT set_config('app.current_tenant_id', '{{BothCasings}}', false);
            INSERT INTO connector_configurations
              (id, tenant_id, connector_name, configuration, secrets, schema_version, last_modified, sys_created_at, sys_updated_at, last_sync_attempt, last_successful_sync, is_healthy)
            VALUES
              ('aaaaaaaa-0000-7000-8000-000000000003', '{{BothCasings}}', 'CareLink',
               '{"server":"US","enabled":true}'::jsonb, '{"refresh_token":"winner"}'::jsonb, 1, now(), now(), now(),
               now(), now(), true),
              ('aaaaaaaa-0000-7000-8000-000000000004', '{{BothCasings}}', 'carelink',
               '{"server":"EU","username":"someone"}'::jsonb, '{"client_id":"kept"}'::jsonb, 1, now(), now(), now(),
               now(), NULL, true),
              ('aaaaaaaa-0000-7000-8000-000000000005', '{{BothCasings}}', 'glooko',
               '{"enabled":true}'::jsonb, '{"password":"q"}'::jsonb, 1, now(), now(), now(),
               NULL, NULL, true);

            SELECT set_config('app.current_tenant_id', '', false);
            """);
    }
}

/// <inheritdoc cref="ConnectorNameCasingFixture"/>
[Trait("Category", "Integration")]
public class ConnectorNameCasingTests(ConnectorNameCasingFixture fixture)
    : IClassFixture<ConnectorNameCasingFixture>
{
    private static readonly Guid Canonical = ConnectorNameCasingFixture.Canonical;
    private static readonly Guid MisCased = ConnectorNameCasingFixture.MisCased;
    private static readonly Guid BothCasings = ConnectorNameCasingFixture.BothCasings;

    [Fact]
    public async Task A_row_written_under_the_other_spelling_is_renamed()
    {
        var name = await fixture.ScalarAsync<string>(MisCased,
            "SELECT connector_name FROM connector_configurations;");
        var secrets = await fixture.ScalarAsync<string>(MisCased,
            "SELECT secrets::text FROM connector_configurations;");

        name.Should().Be("carelink");
        secrets.Should().Contain("refresh_token");
    }

    [Fact]
    public async Task An_already_canonical_row_is_left_alone()
    {
        var name = await fixture.ScalarAsync<string>(Canonical,
            "SELECT connector_name FROM connector_configurations;");

        name.Should().Be("dexcom");
    }

    [Fact]
    public async Task Both_spellings_collapse_onto_the_row_that_last_synced()
    {
        var rows = await fixture.ScalarAsync<long>(BothCasings,
            "SELECT count(*) FROM connector_configurations WHERE connector_name = 'carelink';");
        var configuration = await fixture.ScalarAsync<string>(BothCasings,
            "SELECT configuration::text FROM connector_configurations WHERE connector_name = 'carelink';");
        var secrets = await fixture.ScalarAsync<string>(BothCasings,
            "SELECT secrets::text FROM connector_configurations WHERE connector_name = 'carelink';");

        rows.Should().Be(1);
        configuration.Should().Contain("\"server\": \"US\"",
            "the surviving row's own keys win over the ones it absorbs");
        configuration.Should().Contain("someone",
            "a key only the absorbed row held is the reason to merge rather than drop it");
        secrets.Should().Contain("winner").And.Contain("kept");
    }

    [Fact]
    public async Task A_connector_the_tenant_has_only_once_is_untouched()
    {
        var secrets = await fixture.ScalarAsync<string>(BothCasings,
            "SELECT secrets::text FROM connector_configurations WHERE connector_name = 'glooko';");

        secrets.Should().Contain("\"q\"");
    }

    [Fact]
    public async Task An_insert_under_the_other_spelling_is_rejected()
    {
        var error = await fixture.AttemptAsync(Canonical,
            """
            INSERT INTO connector_configurations
              (id, tenant_id, connector_name, configuration, secrets, schema_version, last_modified, sys_created_at, sys_updated_at, is_healthy)
            VALUES
              (gen_random_uuid(), '11111111-1111-7111-8111-111111111111', 'CareLink',
               '{}'::jsonb, '{}'::jsonb, 1, now(), now(), now(), true);
            """);

        error.Should().NotBeNull("an instance still running the old code must fail loudly rather "
            + "than write a row no lookup can find");
        error!.ConstraintName.Should().Be("ck_connector_configurations_connector_name_lower");
    }

    [Fact]
    public async Task A_rename_to_the_other_spelling_is_rejected()
    {
        var error = await fixture.AttemptAsync(Canonical,
            "UPDATE connector_configurations SET connector_name = 'Dexcom';");

        error.Should().NotBeNull();
        error!.ConstraintName.Should().Be("ck_connector_configurations_connector_name_lower");
    }
}
