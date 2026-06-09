using System.ComponentModel.DataAnnotations.Schema;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Security;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// Behavioural assertions that the per-category public-share RLS policy
/// (<see cref="ShareRlsPolicy.PolicyName"/>) actually enforces category visibility in
/// PostgreSQL: a public share (<c>app.is_share</c>='true') sees a governed table only when
/// its scope is in <c>app.visible_categories</c>, a hidden table is never visible to a share,
/// a share with no/empty CSV is denied all categorized data (fail-closed), and a non-share
/// owner sees everything. Uses raw Npgsql so the assertions cover what Postgres does, not EF.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class RlsShareCategoryTests
{
    private readonly RlsCompletenessFixture _fx;

    // A governed table (stepcount.read) and a hidden table (no governing scope), both with
    // simple NOT NULL columns. The rules are about category gating, not the data itself.
    private const string GovernedTable = "step_counts";
    private const string GovernedScope = "stepcount.read";
    private const string HiddenTable = "body_weights";
    // boluses/treatments.read is the category that originally leaked to glucose-only shares.
    private const string TreatmentTable = "boluses";

    public RlsShareCategoryTests(RlsCompletenessFixture fx) => _fx = fx;

    [Fact]
    public async Task Share_WithMatchingCategory_SeesGovernedTable_ButNotHidden()
    {
        var tenant = Guid.NewGuid();
        await SeedAsync(tenant);

        await using var conn = await _fx.OpenAppConnectionAsync();
        await SetShareContextAsync(conn, tenant, isShare: true, visibleCategories: GovernedScope);

        (await CountAsync(conn, GovernedTable, tenant)).Should().Be(1,
            "a share granted stepcount.read must see step_counts rows");
        (await CountAsync(conn, HiddenTable, tenant)).Should().Be(0,
            "a hidden table (no governing scope) is never visible to a share");
    }

    [Fact]
    public async Task Share_TreatmentsScope_SeesBoluses_ButGlucoseShareDoesNot()
    {
        // The exact category that originally leaked: a treatments share sees boluses; a
        // glucose-only share must not. Proves the treatments.read -> boluses map wiring,
        // independent of the stepcount.read path the other tests exercise.
        var tenant = Guid.NewGuid();
        await SeedAsync(tenant);

        await using var conn = await _fx.OpenAppConnectionAsync();

        await SetShareContextAsync(conn, tenant, isShare: true, visibleCategories: OAuthScopes.TreatmentsRead);
        (await CountAsync(conn, TreatmentTable, tenant)).Should().Be(1,
            "a share granted treatments.read must see boluses");

        await SetShareContextAsync(conn, tenant, isShare: true, visibleCategories: OAuthScopes.GlucoseRead);
        (await CountAsync(conn, TreatmentTable, tenant)).Should().Be(0,
            "a glucose-only share must not see boluses (the original leak)");
    }

    [Fact]
    public async Task Share_WithNonMatchingCategory_IsDeniedGovernedTable()
    {
        var tenant = Guid.NewGuid();
        await SeedAsync(tenant);

        await using var conn = await _fx.OpenAppConnectionAsync();
        await SetShareContextAsync(conn, tenant, isShare: true, visibleCategories: OAuthScopes.GlucoseRead);

        (await CountAsync(conn, GovernedTable, tenant)).Should().Be(0,
            "a glucose-only share must not see stepcount data");
    }

    [Fact]
    public async Task Share_WithEmptyCsv_IsDeniedAllCategorized_FailClosed()
    {
        var tenant = Guid.NewGuid();
        await SeedAsync(tenant);

        await using var conn = await _fx.OpenAppConnectionAsync();
        await SetShareContextAsync(conn, tenant, isShare: true, visibleCategories: string.Empty);

        (await CountAsync(conn, GovernedTable, tenant)).Should().Be(0,
            "a share with no resolved categories must be denied all categorized data (fail-closed)");
        (await CountAsync(conn, HiddenTable, tenant)).Should().Be(0);
    }

    [Fact]
    public async Task NonShare_Owner_SeesEverything()
    {
        var tenant = Guid.NewGuid();
        await SeedAsync(tenant);

        await using var conn = await _fx.OpenAppConnectionAsync();
        // is_share='false' (or unset) — the owner is never restricted by the share policy.
        await SetShareContextAsync(conn, tenant, isShare: false, visibleCategories: string.Empty);

        (await CountAsync(conn, GovernedTable, tenant)).Should().Be(1, "the owner sees their own stepcount data");
        (await CountAsync(conn, HiddenTable, tenant)).Should().Be(1, "the owner sees their own hidden-category data");
    }

    [Fact]
    public async Task Share_HiddenTable_IsDeniedEvenWithBroadCategories()
    {
        var tenant = Guid.NewGuid();
        await SeedAsync(tenant);

        await using var conn = await _fx.OpenAppConnectionAsync();
        await SetShareContextAsync(conn, tenant, isShare: true,
            visibleCategories: "glucose.read,treatments.read,stepcount.read,devices.read");

        (await CountAsync(conn, HiddenTable, tenant)).Should().Be(0,
            "a hidden table has no governing scope, so no granted category can unlock it");
    }

    [Fact]
    public async Task EveryTenantScopedTable_HasCorrectRestrictiveSelectSharePolicy()
    {
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT polrelid::regclass::text, polpermissive, polcmd, pg_get_expr(polqual, polrelid)
            FROM pg_policy WHERE polname = '{ShareRlsPolicy.PolicyName}'
            """;

        var policied = new Dictionary<string, (bool Permissive, char Cmd, string Using)>(StringComparer.Ordinal);
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                policied[reader.GetString(0)] = (reader.GetBoolean(1), reader.GetChar(2), reader.GetString(3));
        }

        foreach (var table in TenantScopedTableNames())
        {
            policied.Should().ContainKey(table,
                $"the reconciler must apply '{ShareRlsPolicy.PolicyName}' to every tenant-scoped table");
            var (permissive, command, usingExpr) = policied[table];
            permissive.Should().BeFalse($"{table}'s share policy must be RESTRICTIVE");
            command.Should().Be('r', $"{table}'s share policy must be FOR SELECT");

            // Inspect the USING body so a wrong/missing scope can't pass as a present policy.
            usingExpr.Should().Contain("is_share", $"{table}'s policy must gate on app.is_share");
            var scope = ShareDataCategories.GoverningScopeFor(table);
            if (scope is null)
            {
                usingExpr.Should().NotContain("visible_categories",
                    $"{table} is hidden from shares, so its policy must not be unlockable by any category");
            }
            else
            {
                usingExpr.Should().Contain($"'{scope}'", $"{table} must gate on its governing scope {scope}");
            }
        }
    }

    [Fact]
    public async Task Reconciler_IsIdempotent()
    {
        // Re-running the reconciler (every startup does) must not throw or change the policy set.
        var before = await CountSharePoliciesAsync();

        var act = () => DatabaseInitializationExtensions.ReconcileShareRlsPoliciesAsync(
            _fx.MigratorConnectionString, NullLogger.Instance);
        await act.Should().NotThrowAsync();

        (await CountSharePoliciesAsync()).Should().Be(before, "DROP+CREATE must leave exactly one policy per table");
    }

    private async Task<long> CountSharePoliciesAsync()
    {
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pg_policy WHERE polname = '{ShareRlsPolicy.PolicyName}'";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task SeedAsync(Guid tenantId)
    {
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await InsertTenantAsync(conn, tenantId);
        await SetCurrentTenantAsync(conn, tenantId);

        await ExecuteAsync(conn,
            $"INSERT INTO {GovernedTable} (id, tenant_id, timestamp, metric, source, sys_created_at, sys_updated_at) " +
            "VALUES (gen_random_uuid(), @tid, now(), 0, 0, now(), now())", tenantId);
        await ExecuteAsync(conn,
            $"INSERT INTO {HiddenTable} (id, tenant_id, mills, weight_kg, sys_created_at, sys_updated_at) " +
            "VALUES (gen_random_uuid(), @tid, 0, 0, now(), now())", tenantId);
        await ExecuteAsync(conn,
            $"INSERT INTO {TreatmentTable} (id, tenant_id, timestamp, insulin, automatic, bolus_kind, sys_created_at, sys_updated_at) " +
            "VALUES (gen_random_uuid(), @tid, now(), 1.0, false, 'Manual', now(), now())", tenantId);
    }

    private static async Task InsertTenantAsync(NpgsqlConnection conn, Guid tenantId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'rls-test', true, now(), now())
            """;
        AddParam(cmd, "@id", tenantId);
        AddParam(cmd, "@slug", $"rls-{tenantId:N}");
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, Guid tenantId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddParam(cmd, "@tid", tenantId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SetCurrentTenantAsync(NpgsqlConnection conn, Guid tenantId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false)";
        AddParam(cmd, "@tid", tenantId.ToString());
        await cmd.ExecuteScalarAsync();
    }

    private static async Task SetShareContextAsync(
        NpgsqlConnection conn, Guid tenantId, bool isShare, string visibleCategories)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT set_config('app.current_tenant_id', @tid, false), " +
            "set_config('app.is_share', @share, false), " +
            "set_config('app.visible_categories', @cats, false)";
        AddParam(cmd, "@tid", tenantId.ToString());
        AddParam(cmd, "@share", isShare ? "true" : "false");
        AddParam(cmd, "@cats", visibleCategories);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(NpgsqlConnection conn, string table, Guid tenantId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE tenant_id = @tid";
        AddParam(cmd, "@tid", tenantId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static void AddParam(NpgsqlCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static IEnumerable<string> TenantScopedTableNames() =>
        typeof(ITenantScoped).Assembly.GetTypes()
            .Where(t => typeof(ITenantScoped).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Select(t => Attribute.GetCustomAttribute(t, typeof(TableAttribute)) as TableAttribute)
            .Where(a => a is not null)
            .Select(a => a!.Name)
            .Distinct(StringComparer.Ordinal);
}
