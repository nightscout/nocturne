using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// What a plan test shares: capturing the SQL a repository sends and explaining it as the
/// connection's role.
/// </summary>
public static class QueryPlans
{
    public static async Task<JsonElement> ExplainAsync(NpgsqlConnection conn, NpgsqlCommand captured)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN (FORMAT JSON) " + captured.CommandText;
        foreach (NpgsqlParameter p in captured.Parameters)
            cmd.Parameters.Add(p.Clone());

        var json = (string)(await cmd.ExecuteScalarAsync())!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>
    /// Plans <paramref name="captured"/> as PostgreSQL may once it keeps one generic plan for a prepared
    /// statement, the worst case: each parameter is a value the planner does not know, so a predicate a
    /// partial index must prove has to hold for the parameter. <see cref="ExplainAsync"/> plans it for
    /// the values it was sent with.
    /// </summary>
    /// <remarks>
    /// <c>EXPLAIN (GENERIC_PLAN)</c> cannot do this through Npgsql: with no parameters bound the bind
    /// message is refused, and with them bound the plan is made for their values.
    /// </remarks>
    public static async Task<JsonElement> ExplainGenericAsync(NpgsqlConnection conn, NpgsqlCommand captured)
    {
        const string statement = "query_plan_generic";
        var parameters = captured.Parameters.Cast<NpgsqlParameter>().ToList();
        var text = captured.CommandText;
        for (var i = 0; i < parameters.Count; i++)
            text = Regex.Replace(text, $@"@{Regex.Escape(parameters[i].ParameterName.TrimStart('@'))}\b", "$$" + (i + 1));

        var types = parameters.Select(p => p.DataTypeName
            ?? throw new InvalidOperationException($"Parameter {p.ParameterName} has no PostgreSQL type name."));

        await using (var prepare = conn.CreateCommand())
        {
            prepare.CommandText =
                $"SET plan_cache_mode = force_generic_plan; PREPARE {statement} ({string.Join(", ", types)}) AS {text}";
            await prepare.ExecuteNonQueryAsync();
        }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"EXPLAIN (FORMAT JSON) EXECUTE {statement}({string.Join(", ", parameters.Select(_ => "NULL"))})";
            var json = (string)(await cmd.ExecuteScalarAsync())!;
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        finally
        {
            await using var cleanup = conn.CreateCommand();
            cleanup.CommandText = $"DEALLOCATE {statement}; RESET plan_cache_mode";
            await cleanup.ExecuteNonQueryAsync();
        }
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
