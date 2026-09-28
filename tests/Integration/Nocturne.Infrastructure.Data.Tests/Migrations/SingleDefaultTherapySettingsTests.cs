using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>SettleSingleDefaultTherapySettings</c> over seeded rows on a real PostgreSQL: a tenant
/// holding a default per profile document keeps only the newest, and a tenant with one keeps it.
/// </summary>
public class SingleDefaultTherapySettingsFixture : IAsyncLifetime
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string PriorMigration = "20260927093001_DropAuthAuditSubjectForeignKeys";

    private string _migratorConnectionString = string.Empty;

    /// <summary>Two documents, each flagging its own default store; the names differ only by case.</summary>
    internal static readonly Guid Duplicated = Guid.Parse("44444444-4444-7444-8444-444444444444");

    /// <summary>A real document's default, and a newer flagged profile-switch snapshot.</summary>
    internal static readonly Guid Switched = Guid.Parse("66666666-6666-7666-8666-666666666666");

    /// <summary>One default, already settled.</summary>
    internal static readonly Guid Settled = Guid.Parse("55555555-5555-7555-8555-555555555555");

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("single_default_therapy");
        _migratorConnectionString = database.MigratorConnectionString;

        await MigrateToAsync(PriorMigration);
        await SeedAsync();
        await MigrateToAsync(targetMigration: null);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string? targetMigration)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;

        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    internal async Task<List<string>> DefaultProfileNamesAsync(Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using (var pin = conn.CreateCommand())
        {
            pin.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
            pin.Parameters.AddWithValue("tenant", tenantId.ToString());
            await pin.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT profile_name FROM therapy_settings WHERE is_default ORDER BY profile_name;";
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    private async Task SeedAsync()
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{{Duplicated}}', 'duplicated', 'Duplicated', true, now(), now()),
              ('{{Settled}}',    'settled',    'Settled',    true, now(), now()),
              ('{{Switched}}',   'switched',   'Switched',   true, now(), now());

            SELECT set_config('app.current_tenant_id', '{{Duplicated}}', false);
            INSERT INTO therapy_settings
              (id, tenant_id, timestamp, legacy_id, profile_name, dia, carbs_hr, delay, is_default,
               is_externally_managed, sys_created_at, sys_updated_at)
            VALUES
              ('bbbbbbbb-0000-7000-8000-000000000001', '{{Duplicated}}', '2026-01-01T00:00:00Z',
               'old:Default', 'Default', 3, 20, 20, true, false, now(), now()),
              ('bbbbbbbb-0000-7000-8000-000000000002', '{{Duplicated}}', '2026-02-01T00:00:00Z',
               'new:default', 'default', 3, 20, 20, true, false, now(), now()),
              ('bbbbbbbb-0000-7000-8000-000000000003', '{{Duplicated}}', '2026-02-01T00:00:00Z',
               'new:Weekend', 'Weekend', 3, 20, 20, false, false, now(), now());

            SELECT set_config('app.current_tenant_id', '{{Settled}}', false);
            INSERT INTO therapy_settings
              (id, tenant_id, timestamp, legacy_id, profile_name, dia, carbs_hr, delay, is_default,
               is_externally_managed, sys_created_at, sys_updated_at)
            VALUES
              ('bbbbbbbb-0000-7000-8000-000000000004', '{{Settled}}', '2026-01-01T00:00:00Z',
               'only:Default', 'Default', 3, 20, 20, true, false, now(), now()),
              ('bbbbbbbb-0000-7000-8000-000000000005', '{{Settled}}', '2026-03-01T00:00:00Z',
               'other:Night', 'Night', 3, 20, 20, false, false, now(), now());

            SELECT set_config('app.current_tenant_id', '{{Switched}}', false);
            INSERT INTO therapy_settings
              (id, tenant_id, timestamp, legacy_id, profile_name, dia, carbs_hr, delay, is_default,
               is_externally_managed, sys_created_at, sys_updated_at)
            VALUES
              ('bbbbbbbb-0000-7000-8000-000000000006', '{{Switched}}', '2026-01-01T00:00:00Z',
               'doc:Default', 'Default', 3, 20, 20, true, false, now(), now()),
              ('bbbbbbbb-0000-7000-8000-000000000007', '{{Switched}}', '2026-04-01T00:00:00Z',
               'switch:Day@@@@@1775001600000', 'Day@@@@@1775001600000', 3, 20, 20, true, false, now(), now());

            SELECT set_config('app.current_tenant_id', '', false);
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <inheritdoc cref="SingleDefaultTherapySettingsFixture"/>
[Trait("Category", "Integration")]
public class SingleDefaultTherapySettingsTests(SingleDefaultTherapySettingsFixture fixture)
    : IClassFixture<SingleDefaultTherapySettingsFixture>
{
    [Fact]
    public async Task A_default_per_document_collapses_onto_the_newest()
    {
        var defaults = await fixture.DefaultProfileNamesAsync(SingleDefaultTherapySettingsFixture.Duplicated);

        defaults.Should().Equal(["default"]);
    }

    [Fact]
    public async Task A_single_default_is_left_alone_even_when_older_rows_exist()
    {
        var defaults = await fixture.DefaultProfileNamesAsync(SingleDefaultTherapySettingsFixture.Settled);

        defaults.Should().Equal(["Default"]);
    }

    [Fact]
    public async Task A_newer_profile_switch_snapshot_does_not_take_the_default()
    {
        var defaults = await fixture.DefaultProfileNamesAsync(SingleDefaultTherapySettingsFixture.Switched);

        defaults.Should().Equal(["Default"]);
    }
}
