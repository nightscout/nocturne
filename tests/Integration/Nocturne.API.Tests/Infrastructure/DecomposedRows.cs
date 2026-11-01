using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nocturne.Core.Models;
using Npgsql;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// The rows a decomposition left in a set of tables, read so that two runs that wrote the same rows
/// compare equal: every id is replaced with a name built from what its row describes.
/// </summary>
internal static partial class DecomposedRows
{
    /// <summary>The columns that name a row or stamp its write.</summary>
    private static readonly HashSet<string> Unnormalised = ["id", "tenant_id", "created_at", "updated_at", "modified_at"];

    /// <summary>
    /// What a row a user deleted is read by: the identity it holds against a re-upload.
    /// </summary>
    private static readonly HashSet<string> DeletedRowKeys =
        ["legacy_id", "original_id", "data_source", "sync_identifier", "deleted_at", "deleted_by_user"];

    public static async Task TruncateAsync(NpgsqlConnection conn, IEnumerable<string> tables)
    {
        await using var truncate = conn.CreateCommand();
        truncate.CommandText = $"TRUNCATE TABLE {string.Join(", ", tables)}, linked_records CASCADE";
        await truncate.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The tenant's rows of each table: the live ones and the ones a user deleted. A row the system
    /// removed is left out, since no read serves it.
    /// </summary>
    /// <param name="liveOnly">Leaves out the rows a user deleted as well.</param>
    public static async Task<Dictionary<string, List<JsonObject>>> ReadAsync(
        NpgsqlConnection conn, Guid tenantId, IEnumerable<string> tables, bool liveOnly = false)
    {
        var rows = new Dictionary<string, List<JsonObject>>();
        foreach (var table in tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT row_to_json(t)::text FROM {table} t WHERE tenant_id = @tenant";
            cmd.Parameters.AddWithValue("tenant", tenantId);
            await using var reader = await cmd.ExecuteReaderAsync();
            var list = new List<JsonObject>();
            while (await reader.ReadAsync())
            {
                var row = JsonNode.Parse(reader.GetString(0))!.AsObject();
                if (row["deleted_at"] is null || (!liveOnly && row["deleted_by_user"]?.GetValue<bool>() == true))
                    list.Add(row);
            }
            rows[table] = list;
        }

        return rows;
    }

    /// <summary>
    /// Names every row by <paramref name="nameOf"/>, and each correlation id by the least name among
    /// the rows carrying it, then replaces every id in every value with its name. Unnormalised and
    /// <c>sys_</c> columns are dropped, a deletion stamp reads only as deleted, and a deleted row reads
    /// only by the identity it holds.
    /// </summary>
    /// <param name="nameOf">The name of a row of a table, from what it describes; rows named after
    /// others' names come later in <paramref name="order"/> and may read them through the renamer.</param>
    /// <param name="order">The tables in the order their rows are named.</param>
    public static Dictionary<string, List<string>> Normalise(
        Dictionary<string, List<JsonObject>> rows,
        IReadOnlyList<string> order,
        Func<string, JsonObject, Func<string?, string?>, string> nameOf)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in order)
        {
            foreach (var row in rows[table])
            {
                var name = $"{table}:{nameOf(table, row, Rename)}|{(row["deleted_at"] is null ? "live" : "deleted")}";
                var id = (string)row["id"]!;
                names[id] = name;
                names[MongoObjectId.FromGuid(Guid.Parse(id))] = name;
                if ((string?)row["correlation_id"] is { } correlation
                    && (!groups.TryGetValue(correlation, out var group) || string.CompareOrdinal(name, group) < 0))
                    groups[correlation] = name;
            }
        }

        foreach (var (correlation, name) in groups)
            names[correlation] = $"group:{name}";

        return rows.ToDictionary(
            t => t.Key,
            t => t.Value
                .Select(row => new JsonObject(row
                    .Where(p => !Unnormalised.Contains(p.Key) && !p.Key.StartsWith("sys_", StringComparison.Ordinal)
                        && (row["deleted_at"] is null || DeletedRowKeys.Contains(p.Key)))
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => KeyValuePair.Create(p.Key, p.Key == "deleted_at"
                        ? (p.Value is null ? null : JsonValue.Create("deleted"))
                        : Renamed(p.Value)))).ToJsonString())
                .Order(StringComparer.Ordinal)
                .ToList());

        string? Rename(string? value) =>
            value is null ? null : IdPattern().Replace(value, m => names.TryGetValue(m.Value, out var name) ? name : m.Value);

        JsonNode? Renamed(JsonNode? node) => node switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(Rename(text)),
            JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Renamed(p.Value)))),
            JsonArray array => new JsonArray([.. array.Select(Renamed)]),
            _ => node?.DeepClone(),
        };
    }

    /// <summary>Each table whose rows differ, with the rows missing from <paramref name="got"/> and the extra ones; empty when none differ.</summary>
    public static string Differences(
        Dictionary<string, List<string>> want, Dictionary<string, List<string>> got, IEnumerable<string> tables)
    {
        var differences = new StringBuilder();
        foreach (var table in tables)
        {
            var missing = want[table].Except(got[table]).ToList();
            var extra = got[table].Except(want[table]).ToList();
            if (missing.Count == 0 && extra.Count == 0 && want[table].Count == got[table].Count)
                continue;
            differences.AppendLine($"{table}: {want[table].Count} rows expected, {got[table].Count} written");
            foreach (var row in missing)
                differences.AppendLine($"  - {row}");
            foreach (var row in extra)
                differences.AppendLine($"  + {row}");
        }

        return differences.ToString();
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|\\b[0-9a-f]{24}\\b")]
    private static partial Regex IdPattern();
}
