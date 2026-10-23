using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>OAuthClientPerRegistration</c> over oauth_clients seeded for two tenants on a real
/// PostgreSQL, under the migrator role and FORCE ROW LEVEL SECURITY.
/// </summary>
public class OAuthClientPerRegistrationFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261022102007_ReclassifyAutomaticBolusesAsAlgorithm";

    internal static readonly Guid TenantA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid Owner = Guid.Parse("33333333-3333-7333-8333-333333333333");

    private string _migratorConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("oauth_client_per_registration");
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

    private async Task<NpgsqlConnection> OpenForTenantAsync(Guid tenantId)
    {
        var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
        cmd.Parameters.AddWithValue("tenant", tenantId.ToString());
        await cmd.ExecuteNonQueryAsync();
        return conn;
    }

    internal async Task<string> ClientsAsync(Guid tenantId)
    {
        await using var conn = await OpenForTenantAsync(tenantId);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT string_agg(client_id, ',' ORDER BY client_id) FROM oauth_clients;";
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Rolled back, so the other tests see the tenant's rows as the migration left them.</summary>
    internal async Task RegisterTwiceAsync(Guid tenantId, string softwareId)
    {
        await using var conn = await OpenForTenantAsync(tenantId);
        await using var transaction = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO oauth_clients (id, tenant_id, client_id, software_id, is_known, redirect_uris)
            VALUES (gen_random_uuid(), @tenant, gen_random_uuid()::text, @software, true, '[]'),
                   (gen_random_uuid(), @tenant, gen_random_uuid()::text, @software, true, '[]');
            """;
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("software", softwareId);
        await cmd.ExecuteNonQueryAsync();
        await transaction.RollbackAsync();
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
    /// Four seeds of directory entries: one with a grant, one with an authorisation code, one named by
    /// a device code, and one nothing uses. Beside them a verified dynamic registration, a
    /// self-registered client and the Scalar docs client, none of them used.
    /// </summary>
    private static string TenantRows(Guid tenant) => $$"""
        SELECT set_config('app.current_tenant_id', '{{tenant}}', false);
        CREATE TEMP TABLE IF NOT EXISTS seed_ids (name text PRIMARY KEY, id uuid);
        TRUNCATE seed_ids;
        INSERT INTO seed_ids VALUES ('granted', gen_random_uuid()), ('auth-code', gen_random_uuid());
        INSERT INTO oauth_clients (id, tenant_id, client_id, software_id, display_name, is_known, redirect_uris, created_from_ip) VALUES
          ((SELECT id FROM seed_ids WHERE name = 'granted'), '{{tenant}}', 'a-seed-granted', 'org.nightscout.trio', 'Trio', true,
           '["org.nightscout.trio://oauth/callback"]', NULL),
          ((SELECT id FROM seed_ids WHERE name = 'auth-code'), '{{tenant}}', 'b-seed-auth-code', 'org.loopkit.loop', 'Loop', true,
           '["org.loopkit.loop://oauth/callback"]', NULL),
          (gen_random_uuid(), '{{tenant}}', 'c-seed-device-code', 'io.home-assistant.nocturne', 'Home Assistant', true, '[]', NULL),
          (gen_random_uuid(), '{{tenant}}', 'd-seed-unused', 'se.cornixit.nightwatch', 'Nightwatch', true, '[]', NULL),
          (gen_random_uuid(), '{{tenant}}', 'e-registered-verified', 'com.eveningoutpost.dexdrip', 'xDrip+', true,
           '["com.eveningoutpost.dexdrip://oauth/callback"]', '203.0.113.7'),
          (gen_random_uuid(), '{{tenant}}', 'f-self-registered', NULL, 'Self', false, '[]', NULL),
          (gen_random_uuid(), '{{tenant}}', 'g-scalar', 'dev.nocturne.scalar', 'Scalar API Reference', false,
           '["https://tenant.example/scalar/oauth2-redirect"]', NULL);
        INSERT INTO oauth_grants (id, tenant_id, client_id, subject_id, grant_type, scopes, created_at) VALUES
          (gen_random_uuid(), '{{tenant}}', (SELECT id FROM seed_ids WHERE name = 'granted'), '{{Owner}}', 'app', ARRAY['glucose.read'], now());
        INSERT INTO oauth_authorization_codes (id, tenant_id, client_entity_id, subject_id, code_hash, scopes, redirect_uri, code_challenge, expires_at, created_at)
        VALUES (gen_random_uuid(), '{{tenant}}', (SELECT id FROM seed_ids WHERE name = 'auth-code'), '{{Owner}}', md5('{{tenant}}'),
                ARRAY['glucose.read'], 'org.loopkit.loop://oauth/callback', 'challenge', now() + interval '10 minutes', now());
        INSERT INTO oauth_device_codes (id, tenant_id, client_id, device_code_hash, user_code, scopes, expires_at, created_at)
        VALUES (gen_random_uuid(), '{{tenant}}', 'c-seed-device-code', md5('{{tenant}}'), left(md5('{{tenant}}'), 8),
                ARRAY['glucose.read'], now() + interval '10 minutes', now());
        """;
}

/// <inheritdoc cref="OAuthClientPerRegistrationFixture"/>
[Trait("Category", "Integration")]
public class OAuthClientPerRegistrationTests(OAuthClientPerRegistrationFixture fixture)
    : IClassFixture<OAuthClientPerRegistrationFixture>
{
    public static TheoryData<Guid> Tenants =>
        [OAuthClientPerRegistrationFixture.TenantA, OAuthClientPerRegistrationFixture.TenantB];

    [Theory]
    [MemberData(nameof(Tenants))]
    public async Task Only_a_seed_nothing_uses_is_deleted(Guid tenant)
    {
        var clients = await fixture.ClientsAsync(tenant);

        clients.Should().Be(
            "a-seed-granted,b-seed-auth-code,c-seed-device-code,e-registered-verified,f-self-registered,g-scalar");
    }

    [Fact]
    public async Task Two_clients_may_share_a_software_id()
    {
        var register = () => fixture.RegisterTwiceAsync(OAuthClientPerRegistrationFixture.TenantA, "com.nocturne.tray");

        await register.Should().NotThrowAsync();
    }
}
