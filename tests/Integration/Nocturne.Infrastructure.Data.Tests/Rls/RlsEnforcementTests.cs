using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// Behavioural assertions that Row Level Security enforces tenant isolation on
/// a representative tenant-scoped table. Uses raw NpgsqlConnection (not EF) so
/// these tests cover what PostgreSQL actually does, independent of the ORM.
///
/// Tenants are generated per test, so the shared fixture is safe to reuse —
/// each test only asserts against rows it inserted.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class RlsEnforcementTests
{
    private readonly RlsCompletenessFixture _fx;

    // Small tenant-scoped table with simple NOT NULL columns. Switching it out
    // doesn't change any assertion — the rules are about RLS, not body weight.
    private const string SampleTable = "body_weights";

    public RlsEnforcementTests(RlsCompletenessFixture fx)
    {
        _fx = fx;
    }

    [Fact]
    public async Task AllTenantScopedTables_HaveRlsEnabledAndForcedAndPolicied()
    {
        var tenantScopedTables = TenantScopedTableNames().ToArray();
        tenantScopedTables.Should().NotBeEmpty(
            "the EF model should declare at least one ITenantScoped entity");

        await using var conn = await _fx.OpenMigratorConnectionAsync();

        var act = () => DatabaseInitializationExtensions.VerifyRlsAsync(
            conn,
            tenantScopedTables,
            NullLogger.Instance);

        await act.Should().NotThrowAsync(
            "VerifyRlsAsync is the canonical schema fingerprint — failing means a tenant-scoped table is missing RLS, FORCE RLS, a policy, or correct ownership");
    }

    [Fact]
    public async Task AppRole_WithoutTenantContext_SeesZeroRows()
    {
        var tenant = Guid.NewGuid();
        await SeedRowAsync(tenant);

        await using var conn = await _fx.OpenAppConnectionAsync();
        var visible = await CountForTenantAsync(conn, tenant);

        visible.Should().Be(0,
            "RLS must filter all rows when app.current_tenant_id is unset");
    }

    [Fact]
    public async Task AppRole_WithTenantA_CannotSeeTenantB_Rows()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await SeedRowAsync(tenantA);
        await SeedRowAsync(tenantB);

        await using var conn = await _fx.OpenAppConnectionAsync();
        await SetCurrentTenantAsync(conn, tenantA);

        var visibleA = await CountForTenantAsync(conn, tenantA);
        var visibleB = await CountForTenantAsync(conn, tenantB);

        visibleA.Should().Be(1, "tenant A's own row must remain visible");
        visibleB.Should().Be(0, "tenant B's row must be hidden from tenant A");
    }

    [Fact]
    public async Task AppRole_InsertWithWrongTenantId_Throws42501()
    {
        var sessionTenant = Guid.NewGuid();
        var wrongTenant = Guid.NewGuid();

        await using var conn = await _fx.OpenAppConnectionAsync();
        await SetCurrentTenantAsync(conn, sessionTenant);

        var act = () => InsertRowAsync(conn, wrongTenant);

        var thrown = await act.Should().ThrowAsync<PostgresException>();
        thrown.Which.SqlState.Should().Be(
            "42501",
            "RLS WITH CHECK violations surface as SQLSTATE 42501 (insufficient privilege)");
    }

    [Fact]
    public async Task MigratorRole_WithoutTenantContext_ObeysForceRls()
    {
        var tenant = Guid.NewGuid();
        await SeedRowAsync(tenant);

        await using var conn = await _fx.OpenMigratorConnectionAsync();
        var visible = await CountForTenantAsync(conn, tenant);

        visible.Should().Be(0,
            "FORCE ROW LEVEL SECURITY must apply to the table owner, not just non-owner roles");
    }

    [Fact]
    public async Task AppRole_IsNotSuperuserAndDoesNotBypassRls()
    {
        await using var conn = await _fx.OpenAppConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT current_user, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user";

        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.GetString(0).Should().Be("nocturne_app");
        reader.GetBoolean(1).Should().BeFalse("nocturne_app must not be a superuser");
        reader.GetBoolean(2).Should().BeFalse("nocturne_app must not have BYPASSRLS");
    }

    private static IEnumerable<string> TenantScopedTableNames()
    {
        return typeof(ITenantScoped).Assembly
            .GetTypes()
            .Where(t => typeof(ITenantScoped).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Select(t => (System.ComponentModel.DataAnnotations.Schema.TableAttribute?)
                Attribute.GetCustomAttribute(t, typeof(System.ComponentModel.DataAnnotations.Schema.TableAttribute)))
            .Where(attr => attr is not null)
            .Select(attr => attr!.Name)
            .Distinct(StringComparer.Ordinal);
    }

    private async Task SeedRowAsync(Guid tenantId)
    {
        // body_weights.tenant_id has a foreign key to tenants.Id, so the tenant
        // row must exist before the sample row will accept the FK.
        // Migrator obeys FORCE RLS too, so set the GUC before the body_weights
        // INSERT or the WITH CHECK clause rejects it. The tenants table itself
        // isn't tenant-scoped so the tenant insert doesn't need a GUC.
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await InsertTenantAsync(conn, tenantId);
        await SetCurrentTenantAsync(conn, tenantId);
        await InsertRowAsync(conn, tenantId);
    }

    private static async Task InsertTenantAsync(NpgsqlConnection conn, Guid tenantId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants
                (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES
                (@id, @slug, 'rls-test', true, now(), now())
            """;
        var idParam = cmd.CreateParameter();
        idParam.ParameterName = "@id";
        idParam.Value = tenantId;
        cmd.Parameters.Add(idParam);

        var slugParam = cmd.CreateParameter();
        slugParam.ParameterName = "@slug";
        slugParam.Value = $"rls-{tenantId:N}";
        cmd.Parameters.Add(slugParam);

        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertRowAsync(NpgsqlConnection conn, Guid rowTenantId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {SampleTable}
                (id, tenant_id, mills, weight_kg, sys_created_at, sys_updated_at)
            VALUES
                (gen_random_uuid(), @tid, 0, 0, now(), now())
            """;
        var p = cmd.CreateParameter();
        p.ParameterName = "@tid";
        p.Value = rowTenantId;
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SetCurrentTenantAsync(NpgsqlConnection conn, Guid tenantId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false)";
        var p = cmd.CreateParameter();
        p.ParameterName = "@tid";
        p.Value = tenantId.ToString();
        cmd.Parameters.Add(p);
        await cmd.ExecuteScalarAsync();
    }

    private static async Task<long> CountForTenantAsync(NpgsqlConnection conn, Guid tenantId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {SampleTable} WHERE tenant_id = @tid";
        var p = cmd.CreateParameter();
        p.ParameterName = "@tid";
        p.Value = tenantId;
        cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }
}
