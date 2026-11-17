using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// What a plan test shares: capturing the SQL a repository sends and explaining it as the
/// connection's role.
/// </summary>
internal static class PlanTestSupport
{
    internal static async Task SetTenantAsync(NpgsqlConnection conn, Guid tenant)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false), set_config('app.is_share', 'false', false)";
        cmd.Parameters.AddWithValue("tid", tenant.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<JsonElement> ExplainAsync(NpgsqlConnection conn, NpgsqlCommand captured)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN (FORMAT JSON) " + captured.CommandText;
        foreach (NpgsqlParameter p in captured.Parameters)
            cmd.Parameters.Add(p.Clone());

        var json = (string)(await cmd.ExecuteScalarAsync())!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    internal static IEnumerable<string> Descendants(this JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (p.Name == property && p.Value.ValueKind == JsonValueKind.String)
                    yield return p.Value.GetString()!;
                foreach (var d in p.Value.Descendants(property))
                    yield return d;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            foreach (var d in item.Descendants(property))
                yield return d;
        }
    }

    internal sealed class LastReaderCapture : DbCommandInterceptor
    {
        public NpgsqlCommand? Command { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var copy = new NpgsqlCommand(command.CommandText);
            foreach (NpgsqlParameter p in command.Parameters)
                copy.Parameters.Add(p.Clone());
            Command = copy;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

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

        await PlanTestSupport.SetTenantAsync(conn, tenant);

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
