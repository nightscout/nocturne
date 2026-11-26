using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// What a plan test shares: capturing the SQL a repository sends and explaining it as the
/// connection's role.
/// </summary>
public static class QueryPlans
{
    public static async Task SetTenantAsync(NpgsqlConnection conn, Guid tenant)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false), set_config('app.is_share', 'false', false)";
        cmd.Parameters.AddWithValue("tid", tenant.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<JsonElement> ExplainAsync(NpgsqlConnection conn, NpgsqlCommand captured)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN (FORMAT JSON) " + captured.CommandText;
        foreach (NpgsqlParameter p in captured.Parameters)
            cmd.Parameters.Add(p.Clone());

        var json = (string)(await cmd.ExecuteScalarAsync())!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public static IEnumerable<string> Descendants(this JsonElement element, string property)
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

    public sealed class LastReaderCapture : DbCommandInterceptor
    {
        public NpgsqlCommand? Command => Commands.Count == 0 ? null : Commands[^1];

        /// <summary>Every reader command sent, in order, for a read that sends several.</summary>
        public List<NpgsqlCommand> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var copy = new NpgsqlCommand(command.CommandText);
            foreach (NpgsqlParameter p in command.Parameters)
                copy.Parameters.Add(p.Clone());
            Commands.Add(copy);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
