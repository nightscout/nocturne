using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>Seeds the rows a plan test explains a read over. The capture and explain are <see cref="QueryPlans"/>.</summary>
internal static class PlanTestSupport
{
    internal static async Task SeedAsync(
        RlsCompletenessFixture fx, Guid tenant, string table, string columns, string values, int rows)
    {
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@tid, @slug, 'plan-test', true, now(), now())
                """;
            cmd.Parameters.AddWithValue("tid", tenant);
            cmd.Parameters.AddWithValue("slug", $"plan-{tenant:N}");
            await cmd.ExecuteNonQueryAsync();
        }

        await QueryPlans.SetTenantAsync(conn, tenant);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO {table} {columns} SELECT {values} FROM generate_series(1, {rows}) g";
            cmd.Parameters.AddWithValue("tid", tenant);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var analyze = conn.CreateCommand();
        analyze.CommandText = $"ANALYZE {table}";
        await analyze.ExecuteNonQueryAsync();
    }
}
