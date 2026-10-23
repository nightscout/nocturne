using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>MarkDeviceApprovedGrants</c> over oauth_grants seeded for two tenants on a real
/// PostgreSQL, under the migrator role and FORCE ROW LEVEL SECURITY.
/// </summary>
public class MarkDeviceApprovedGrantsFixture : IAsyncLifetime
{
    private const string MigrationUnderTest = "20261021093245_MarkDeviceApprovedGrants";
    private const string PriorMigration = "20261021070559_UnverifyKnownClientsWithForeignRedirectUris";

    internal static readonly Guid TenantA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid Owner = Guid.Parse("33333333-3333-7333-8333-333333333333");

    private string _migratorConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("mark_device_approved_grants");
        _migratorConnectionString = database.MigratorConnectionString;

        await MigrateToAsync(PriorMigration);
        await SeedAsync();
        await MigrateToAsync(MigrationUnderTest);
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

    internal async Task<string> GrantsAsync(Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
        cmd.Parameters.AddWithValue("tenant", tenantId.ToString());
        await cmd.ExecuteNonQueryAsync();

        cmd.Parameters.Clear();
        cmd.CommandText = """
            SELECT string_agg(label || ':' || approved_by_device_code, ',' ORDER BY label)
            FROM oauth_grants;
            """;
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task SeedAsync()
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{{TenantA}}', 'tenant-a', 'Tenant A', true, now(), now()),
              ('{{TenantB}}', 'tenant-b', 'Tenant B', true, now(), now());
            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES ('{{Owner}}', 'Owner', true, false, now(), now(), 'Approved');
            {{TenantRows(TenantA)}}
            {{TenantRows(TenantB)}}
            SELECT set_config('app.current_tenant_id', '', false);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A grant on a known client with no redirect URIs, two on a known client with them (one named
    /// by a device code still on file), one on a self-registered client with none, and a direct
    /// grant with no client at all.
    /// </summary>
    private static string TenantRows(Guid tenant) => $$"""
        SELECT set_config('app.current_tenant_id', '{{tenant}}', false);
        CREATE TEMP TABLE IF NOT EXISTS seed_ids (name text PRIMARY KEY, id uuid);
        TRUNCATE seed_ids;
        INSERT INTO seed_ids VALUES
          ('nightwatch', gen_random_uuid()), ('trio', gen_random_uuid()), ('self', gen_random_uuid()),
          ('trio-device', gen_random_uuid());
        INSERT INTO oauth_clients (id, tenant_id, client_id, software_id, display_name, is_known, redirect_uris) VALUES
          ((SELECT id FROM seed_ids WHERE name = 'nightwatch'), '{{tenant}}', 'nightwatch-{{tenant}}',
           'se.cornixit.nightwatch', 'Nightwatch', true, '[]'),
          ((SELECT id FROM seed_ids WHERE name = 'trio'), '{{tenant}}', 'trio-{{tenant}}',
           'org.nightscout.trio', 'Trio', true, '["org.nightscout.trio://oauth/callback"]'),
          ((SELECT id FROM seed_ids WHERE name = 'self'), '{{tenant}}', 'self-{{tenant}}',
           NULL, 'Self', false, '[]');
        INSERT INTO oauth_grants (id, tenant_id, client_id, subject_id, grant_type, scopes, label, created_at) VALUES
          (gen_random_uuid(), '{{tenant}}', (SELECT id FROM seed_ids WHERE name = 'nightwatch'), '{{Owner}}', 'app', ARRAY['glucose.read'], 'a-nightwatch', now()),
          (gen_random_uuid(), '{{tenant}}', (SELECT id FROM seed_ids WHERE name = 'trio'), '{{Owner}}', 'app', ARRAY['glucose.read'], 'b-trio', now()),
          ((SELECT id FROM seed_ids WHERE name = 'trio-device'), '{{tenant}}', (SELECT id FROM seed_ids WHERE name = 'trio'), '{{Owner}}', 'app', ARRAY['glucose.read'], 'c-trio-device', now()),
          (gen_random_uuid(), '{{tenant}}', (SELECT id FROM seed_ids WHERE name = 'self'), '{{Owner}}', 'app', ARRAY['glucose.read'], 'd-self', now()),
          (gen_random_uuid(), '{{tenant}}', NULL, '{{Owner}}', 'direct', ARRAY['*'], 'e-direct', now());
        INSERT INTO oauth_device_codes (id, tenant_id, client_id, device_code_hash, user_code, scopes, expires_at, created_at, approved_at, grant_id, subject_id)
        VALUES (gen_random_uuid(), '{{tenant}}', 'trio-{{tenant}}', md5('{{tenant}}'), left(md5('{{tenant}}'), 8),
                ARRAY['glucose.read'], now() + interval '10 minutes', now(), now(),
                (SELECT id FROM seed_ids WHERE name = 'trio-device'), '{{Owner}}');
        """;
}

/// <inheritdoc cref="MarkDeviceApprovedGrantsFixture"/>
[Trait("Category", "Integration")]
public class MarkDeviceApprovedGrantsTests(MarkDeviceApprovedGrantsFixture fixture)
    : IClassFixture<MarkDeviceApprovedGrantsFixture>
{
    public static TheoryData<Guid> Tenants =>
        [MarkDeviceApprovedGrantsFixture.TenantA, MarkDeviceApprovedGrantsFixture.TenantB];

    [Theory]
    [MemberData(nameof(Tenants))]
    public async Task Grants_only_the_device_flow_could_have_reached_on_a_known_client_are_marked(Guid tenant)
    {
        var grants = await fixture.GrantsAsync(tenant);

        grants.Should().Be(
            "a-nightwatch:true,b-trio:false,c-trio-device:true,d-self:false,e-direct:false");
    }
}
