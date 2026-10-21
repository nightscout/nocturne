using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>UnverifyKnownClientsWithForeignRedirectUris</c> over oauth_clients seeded for two tenants
/// on a real PostgreSQL, under the migrator role and FORCE ROW LEVEL SECURITY.
/// </summary>
public class UnverifyKnownClientsWithForeignRedirectUrisFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261021070431_AddRefreshTokenAndDeviceCodeClaims";

    internal static readonly Guid TenantA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("22222222-2222-7222-8222-222222222222");

    private string _migratorConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("unverify_known_clients");
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

    internal async Task<string> ClientsAsync(Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
        cmd.Parameters.AddWithValue("tenant", tenantId.ToString());
        await cmd.ExecuteNonQueryAsync();

        cmd.Parameters.Clear();
        cmd.CommandText = """
            SELECT string_agg(client_id || ':' || is_known || ':' || coalesce(software_id, '-'), ',' ORDER BY client_id)
            FROM oauth_clients;
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
            {{TenantRows(TenantA)}}
            {{TenantRows(TenantB)}}
            SELECT set_config('app.current_tenant_id', '', false);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A registration-carrying seed of a directory entry with no redirect URIs, a pristine seed of
    /// another such entry, and a seed of an entry whose directory URIs it carries.
    /// </summary>
    private static string TenantRows(Guid tenant) => $$"""
        SELECT set_config('app.current_tenant_id', '{{tenant}}', false);
        INSERT INTO oauth_clients (id, tenant_id, client_id, software_id, display_name, is_known, redirect_uris)
        VALUES (gen_random_uuid(), '{{tenant}}', 'a-adopted', 'io.home-assistant.nocturne', 'Home Assistant', true,
                '["https://elsewhere.example/callback"]'),
               (gen_random_uuid(), '{{tenant}}', 'b-pristine', 'se.cornixit.nightwatch', 'Nightwatch', true, '[]'),
               (gen_random_uuid(), '{{tenant}}', 'c-trio', 'org.nightscout.trio', 'Trio', true,
                '["org.nightscout.trio://oauth/callback"]');
        """;
}

/// <inheritdoc cref="UnverifyKnownClientsWithForeignRedirectUrisFixture"/>
[Trait("Category", "Integration")]
public class UnverifyKnownClientsWithForeignRedirectUrisTests(UnverifyKnownClientsWithForeignRedirectUrisFixture fixture)
    : IClassFixture<UnverifyKnownClientsWithForeignRedirectUrisFixture>
{
    public static TheoryData<Guid> Tenants =>
        [UnverifyKnownClientsWithForeignRedirectUrisFixture.TenantA, UnverifyKnownClientsWithForeignRedirectUrisFixture.TenantB];

    [Theory]
    [MemberData(nameof(Tenants))]
    public async Task Only_a_seed_carrying_redirect_uris_its_directory_entry_lacks_is_unverified(Guid tenant)
    {
        var clients = await fixture.ClientsAsync(tenant);

        clients.Should().Be(
            "a-adopted:false:-,b-pristine:true:se.cornixit.nightwatch,c-trio:true:org.nightscout.trio");
    }
}
