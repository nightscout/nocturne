using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>MoveAdoptedSecretsToDeviceSubject</c> over seeded rows on a real PostgreSQL.
/// </summary>
/// <remarks>
/// One run against one seed, as in <see cref="SubjectTokenConversionFixture"/>: each tenant below is
/// independent.
/// </remarks>
public class MigratedSecretHolderFixture : IAsyncLifetime
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string PriorMigration = "20261020015113_ConvertMotionClassActivitiesToMotionSpans";

    private string _migratorConnectionString = string.Empty;

    internal static readonly Guid Creates = Guid.Parse("44444444-4444-7444-8444-444444444444");
    internal static readonly Guid Reuses = Guid.Parse("55555555-5555-7555-8555-555555555555");
    internal static readonly Guid LeftAlone = Guid.Parse("66666666-6666-7666-8666-666666666666");

    internal static readonly Guid Owner = Guid.Parse("44444444-0000-7000-8000-00000000a001");
    internal static readonly Guid PlatformAdmin = Guid.Parse("44444444-0000-7000-8000-00000000a002");
    internal static readonly Guid FormerMember = Guid.Parse("44444444-0000-7000-8000-00000000a003");
    internal static readonly Guid ExistingDevices = Guid.Parse("55555555-0000-7000-8000-00000000d001");

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("secret_holder");
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

    internal async Task<T?> ScalarAsync<T>(Guid tenantId, string sql)
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
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    private async Task ExecuteAsync(Guid tenantId, string sql)
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT set_config('app.current_tenant_id', '{tenantId}', false); {sql}";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedAsync()
    {
        await ExecuteAsync(Guid.Empty, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{Creates}',   'creates',   'Creates',   true, now(), now()),
              ('{Reuses}',    'reuses',    'Reuses',    true, now(), now()),
              ('{LeftAlone}', 'leftalone', 'LeftAlone', true, now(), now());

            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status) VALUES
              ('{Owner}',           'Owner',         true, false, now(), now(), 'Approved'),
              ('{PlatformAdmin}',   'PlatformAdmin', true, false, now(), now(), 'Approved'),
              ('{FormerMember}',    'FormerMember',  true, false, now(), now(), 'Approved'),
              ('{ExistingDevices}', 'Devices',       true, true,  now(), now(), 'Approved');

            INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, sys_created_at, sys_updated_at, limit_to_24_hours) VALUES
              (gen_random_uuid(), '{Creates}',   '{Owner}',           '["*"]'::jsonb, now(), now(), false),
              (gen_random_uuid(), '{Reuses}',    '{Owner}',           '["*"]'::jsonb, now(), now(), false),
              (gen_random_uuid(), '{Reuses}',    '{ExistingDevices}', '["*"]'::jsonb, now(), now(), false),
              (gen_random_uuid(), '{LeftAlone}', '{Owner}',           '["*"]'::jsonb, now(), now(), false);
            """);

        await ExecuteAsync(Creates, $"""
            INSERT INTO oauth_grants (id, tenant_id, subject_id, grant_type, scopes, label, legacy_secret_hash, token_hash, is_migrated, revoked_at, created_at) VALUES
              (gen_random_uuid(), '{Creates}', '{PlatformAdmin}', 'direct', ARRAY['health.readwrite'], 'Orphaned', repeat('1',40), NULL, true, NULL, now()),
              (gen_random_uuid(), '{Creates}', '{Owner}',         'direct', ARRAY['*'],                'OwnersOwn', repeat('2',40), NULL, true, NULL, now()),
              (gen_random_uuid(), '{Creates}', '{FormerMember}',  'direct', ARRAY['*'],                'Minted',   repeat('3',40), repeat('3',64), false, NULL, now()),
              (gen_random_uuid(), '{Creates}', '{PlatformAdmin}', 'direct', ARRAY['*'],                'Revoked',  repeat('4',40), NULL, true, now(), now());
            """);

        await ExecuteAsync(Reuses, $"""
            INSERT INTO oauth_grants (id, tenant_id, subject_id, grant_type, scopes, label, legacy_secret_hash, is_migrated, created_at)
            VALUES (gen_random_uuid(), '{Reuses}', '{PlatformAdmin}', 'direct', ARRAY['health.readwrite'], 'Orphaned', repeat('5',40), true, now());
            """);

        await ExecuteAsync(LeftAlone, $"""
            INSERT INTO oauth_grants (id, tenant_id, subject_id, grant_type, scopes, label, legacy_secret_hash, token_hash, is_migrated, created_at)
            VALUES (gen_random_uuid(), '{LeftAlone}', '{Owner}', 'direct', ARRAY['*'], 'Minted', repeat('6',40), repeat('6',64), false, now());
            """);
    }
}

/// <inheritdoc cref="MigratedSecretHolderFixture"/>
[Trait("Category", "Integration")]
public class MigratedSecretHolderTests(MigratedSecretHolderFixture fixture)
    : IClassFixture<MigratedSecretHolderFixture>
{
    private static readonly Guid Creates = MigratedSecretHolderFixture.Creates;
    private static readonly Guid Reuses = MigratedSecretHolderFixture.Reuses;
    private static readonly Guid LeftAlone = MigratedSecretHolderFixture.LeftAlone;

    private Task<T?> ScalarAsync<T>(Guid tenantId, string sql) => fixture.ScalarAsync<T>(tenantId, sql);

    private Task<Guid> HolderAsync(Guid tenantId, string label) =>
        ScalarAsync<Guid>(tenantId, $"SELECT subject_id FROM oauth_grants WHERE label = '{label}';");

    private Task<Guid> DevicesOfAsync(Guid tenantId) =>
        ScalarAsync<Guid>(tenantId,
            "SELECT s.id FROM tenant_members tm JOIN subjects s ON s.id = tm.subject_id"
            + $" WHERE tm.tenant_id = '{tenantId}' AND s.is_system_subject AND s.name = 'Devices';");

    [Fact]
    public async Task A_secret_held_by_a_non_member_moves_to_a_new_full_access_device_subject()
    {
        var devices = await DevicesOfAsync(Creates);
        var permissions = await ScalarAsync<string>(Creates,
            $"SELECT direct_permissions::text FROM tenant_members WHERE tenant_id = '{Creates}' AND subject_id = '{devices}';");

        devices.Should().NotBeEmpty();
        (await HolderAsync(Creates, "Orphaned")).Should().Be(devices);
        permissions.Should().Be("[\"*\"]", "the bound on a device credential is its own scope list");
    }

    [Fact]
    public async Task A_tenant_that_already_has_a_device_subject_reuses_it()
    {
        var holders = await ScalarAsync<long>(Reuses,
            "SELECT count(*) FROM tenant_members tm JOIN subjects s ON s.id = tm.subject_id"
            + $" WHERE tm.tenant_id = '{Reuses}' AND s.is_system_subject AND s.name = 'Devices';");

        holders.Should().Be(1);
        (await HolderAsync(Reuses, "Orphaned")).Should().Be(MigratedSecretHolderFixture.ExistingDevices);
    }

    [Fact]
    public async Task A_secret_a_member_saved_moves_too()
    {
        // Left with the member, removing them would stop every uploader on the site.
        (await HolderAsync(Creates, "OwnersOwn")).Should().Be(await DevicesOfAsync(Creates));
    }

    [Fact]
    public async Task A_minted_key_of_a_former_member_does_not_become_a_site_key()
    {
        (await HolderAsync(Creates, "Minted")).Should().Be(MigratedSecretHolderFixture.FormerMember);
    }

    [Fact]
    public async Task A_revoked_secret_is_left_where_it_is()
    {
        (await HolderAsync(Creates, "Revoked")).Should().Be(MigratedSecretHolderFixture.PlatformAdmin);
    }

    [Fact]
    public async Task A_tenant_with_nothing_to_move_gets_no_device_subject()
    {
        (await DevicesOfAsync(LeftAlone)).Should().BeEmpty();
    }
}
