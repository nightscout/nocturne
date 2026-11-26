using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddWriterGrantId</c> over two tenants whose mutation audit log names the grant, a session
/// cookie or nothing as the creator of their rows, on a real PostgreSQL as the migrator, which row
/// level security binds.
/// </summary>
public class AddWriterGrantIdFixture : IAsyncLifetime
{
    /// <summary>The migration immediately before the one under test.</summary>
    private const string PriorMigration = "20261115015143_AddWriteBackIdentityTracking";

    private string _migratorConnectionString = string.Empty;

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222222");
    internal static readonly Guid Subject = Guid.Parse("e0000000-0000-7000-8000-000000000001");
    internal static readonly Guid GrantInA = Guid.Parse("f0000000-0000-7000-8000-00000000000a");
    internal static readonly Guid LaterGrantInA = Guid.Parse("f0000000-0000-7000-8000-00000000000c");
    internal static readonly Guid GrantInB = Guid.Parse("f0000000-0000-7000-8000-00000000000b");
    internal static readonly Guid SessionJwt = Guid.Parse("f0000000-0000-7000-8000-0000000000ff");

    internal static readonly Guid BolusByGrantInA = Guid.Parse("c0000000-0000-7000-8000-000000000001");
    internal static readonly Guid BolusBySessionInA = Guid.Parse("c0000000-0000-7000-8000-000000000002");
    internal static readonly Guid BolusWithoutAuditInA = Guid.Parse("c0000000-0000-7000-8000-000000000003");
    internal static readonly Guid BolusOnlyUpdatedByGrantInA = Guid.Parse("c0000000-0000-7000-8000-000000000004");
    internal static readonly Guid SpanByGrantInA = Guid.Parse("c0000000-0000-7000-8000-000000000005");
    internal static readonly Guid BolusByGrantInB = Guid.Parse("c0000000-0000-7000-8000-000000000006");

    internal Dictionary<Guid, Guid?> Writers { get; } = [];

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_writer_grant_id");
        _migratorConnectionString = database.MigratorConnectionString;

        await SeedAsync();

        var options = TestContexts.MigrationsOptions(_migratorConnectionString);
        await using (var context = new NocturneDbContext(options))
            await context.GetService<IMigrator>().MigrateAsync();

        await ReadAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task ReadAsync()
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await using (var pin = conn.CreateCommand())
            {
                pin.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
                pin.Parameters.AddWithValue("tenant", tenant.ToString());
                await pin.ExecuteNonQueryAsync();
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, writer_grant_id FROM boluses
                UNION ALL SELECT id, writer_grant_id FROM state_spans;
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                Writers[reader.GetGuid(0)] = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        }
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
            INSERT INTO subjects (id, name) VALUES ('{{Subject}}', 'Owner');

            SELECT set_config('app.current_tenant_id', '{{TenantA}}', false);
            INSERT INTO oauth_grants (id, subject_id, grant_type, scopes, tenant_id) VALUES
              ('{{GrantInA}}', '{{Subject}}', 'direct', ARRAY['*'], '{{TenantA}}'),
              ('{{LaterGrantInA}}', '{{Subject}}', 'direct', ARRAY['*'], '{{TenantA}}');
            INSERT INTO boluses (id, tenant_id, insulin, automatic) VALUES
              ('{{BolusByGrantInA}}', '{{TenantA}}', 1, false),
              ('{{BolusBySessionInA}}', '{{TenantA}}', 1, false),
              ('{{BolusWithoutAuditInA}}', '{{TenantA}}', 1, false),
              ('{{BolusOnlyUpdatedByGrantInA}}', '{{TenantA}}', 1, false);
            INSERT INTO state_spans (id, tenant_id, category, state) VALUES
              ('{{SpanByGrantInA}}', '{{TenantA}}', 'TemporaryTarget', 'Active');
            INSERT INTO mutation_audit_log (id, tenant_id, entity_type, entity_id, action, token_id, created_at) VALUES
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', 'Bolus', '{{BolusByGrantInA}}', 'create', '{{GrantInA}}', now() - interval '2 days'),
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', 'Bolus', '{{BolusByGrantInA}}', 'create', '{{LaterGrantInA}}', now() - interval '1 day'),
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', 'Bolus', '{{BolusBySessionInA}}', 'create', '{{SessionJwt}}', now()),
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', 'Bolus', '{{BolusOnlyUpdatedByGrantInA}}', 'update', '{{GrantInA}}', now()),
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', 'StateSpan', '{{SpanByGrantInA}}', 'create', '{{GrantInA}}', now()),
              ('{{Guid.CreateVersion7()}}', '{{TenantA}}', 'StateSpan', '{{BolusWithoutAuditInA}}', 'create', '{{GrantInA}}', now());

            SELECT set_config('app.current_tenant_id', '{{TenantB}}', false);
            INSERT INTO oauth_grants (id, subject_id, grant_type, scopes, tenant_id) VALUES
              ('{{GrantInB}}', '{{Subject}}', 'direct', ARRAY['*'], '{{TenantB}}');
            INSERT INTO boluses (id, tenant_id, insulin, automatic) VALUES
              ('{{BolusByGrantInB}}', '{{TenantB}}', 1, false);
            INSERT INTO mutation_audit_log (id, tenant_id, entity_type, entity_id, action, token_id, created_at) VALUES
              ('{{Guid.CreateVersion7()}}', '{{TenantB}}', 'Bolus', '{{BolusByGrantInB}}', 'create', '{{GrantInB}}', now());

            SELECT set_config('app.current_tenant_id', '', false);
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <inheritdoc cref="AddWriterGrantIdFixture"/>
[Trait("Category", "Integration")]
public class AddWriterGrantIdTests(AddWriterGrantIdFixture fixture) : IClassFixture<AddWriterGrantIdFixture>
{
    [Fact]
    public void A_row_takes_the_grant_of_its_first_create_in_the_audit_log()
    {
        fixture.Writers[AddWriterGrantIdFixture.BolusByGrantInA].Should().Be(AddWriterGrantIdFixture.GrantInA);
        fixture.Writers[AddWriterGrantIdFixture.SpanByGrantInA].Should().Be(AddWriterGrantIdFixture.GrantInA);
    }

    [Fact]
    public void Each_tenant_is_backfilled_from_its_own_log()
    {
        fixture.Writers[AddWriterGrantIdFixture.BolusByGrantInB].Should().Be(AddWriterGrantIdFixture.GrantInB);
    }

    [Fact]
    public void A_token_id_that_is_no_grant_writes_nothing()
    {
        fixture.Writers[AddWriterGrantIdFixture.BolusBySessionInA].Should().BeNull();
    }

    [Fact]
    public void A_row_the_log_does_not_record_the_creation_of_stays_without_a_writer()
    {
        fixture.Writers[AddWriterGrantIdFixture.BolusWithoutAuditInA].Should().BeNull();
        fixture.Writers[AddWriterGrantIdFixture.BolusOnlyUpdatedByGrantInA].Should().BeNull();
    }
}
