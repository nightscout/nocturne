using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Text.RegularExpressions;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// Guards the join between the model's index declarations and the DDL that is supposed to build
/// them. Several migrations create indexes through <c>migrationBuilder.Sql</c> rather than
/// <c>CreateIndex</c> — CONCURRENTLY cannot be expressed any other way, and EF conflates two
/// indexes over the same property set — which means the model and the database agree only by the
/// author having typed the same thing twice. EF's own pending-model-changes check does not close
/// this: it compares the model against the snapshot, and the snapshot is generated from the model,
/// so a migration whose SQL names the wrong index, table or column, or omits the filter, stays
/// green while production silently lacks what the model claims it has.
/// </summary>
/// <remarks>
/// <para>
/// Reuses the seedless RLS fixture: this inspects schema metadata only.
/// </para>
/// <para>
/// One-directional by construction: it walks what the model declares and looks for it in the
/// database, so it cannot see an index the database has and the model does not — which is the
/// class the duplicated tenant foreign keys belonged to. Access method, <c>INCLUDE</c> columns and
/// <c>NULLS NOT DISTINCT</c> are not compared either; the model declares none of them today.
/// </para>
/// </remarks>
[Collection("RLS completeness")]
[Trait("Category", "Integration")]
public class ModelIndexesMatchDatabaseTests
{
    private readonly RlsCompletenessFixture _fixture;

    public ModelIndexesMatchDatabaseTests(RlsCompletenessFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task EveryModelIndex_ExistsInTheDatabaseWithTheDeclaredShape()
    {
        var declared = DeclaredIndexes();
        declared.Should().HaveCountGreaterThan(150,
            "an empty or tiny set would let the comparison below pass vacuously");

        var actual = await LoadDatabaseIndexesAsync();

        var mismatched = declared
            .Where(d => !actual.TryGetValue(d.Key, out var found) || found != d.Value)
            .Select(d => actual.TryGetValue(d.Key, out var found)
                ? $"{d.Key}: model has {d.Value}, database has {found}"
                : $"{d.Key}: declared on the model, absent from the database")
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

        mismatched.Should().BeEmpty(
            "a migration that builds an index in raw SQL has to build the shape the model "
            + "declares, or the model is describing something production does not have:{0}{1}",
            Environment.NewLine,
            string.Join(Environment.NewLine, mismatched));
    }

    /// <summary>
    /// Every index the model declares, as name -> table, ordered columns and predicate.
    /// <para>
    /// Sort direction is deliberately not compared. Every index here leads with <c>tenant_id</c>
    /// under an equality predicate, so a backward scan serves the other direction and the two
    /// builds are interchangeable — and the estate already disagrees with itself:
    /// <c>ix_boluses_tenant_timestamp</c>, <c>ix_carb_intakes_tenant_timestamp</c> and
    /// <c>ix_temp_basals_tenant_start_timestamp</c> are DESC in production and ASC in a database
    /// built from the same migrations today, because
    /// <c>20260511122202_AddCompositePerformanceIndexes</c> writes DESC behind
    /// <c>CREATE INDEX IF NOT EXISTS</c> over names <c>20260511000001_AddTenantTimestampIndexes</c>
    /// had already created ASC. Asserting direction would fail on that history rather than on new
    /// drift.
    /// </para>
    /// </summary>
    private static Dictionary<string, string> DeclaredIndexes()
    {
        using var context = new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>()
                .UseNpgsql("Host=localhost;Database=nocturne;Username=test;Password=test")
                .Options);

        // The runtime model drops IsDescending and GetFilter; only the design-time one keeps them.
        return context.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .SelectMany(e => e.GetIndexes())
            .DistinctBy(i => i.GetDatabaseName())
            .ToDictionary(
                i => i.GetDatabaseName()!,
                i => Shape(
                    i.DeclaringEntityType.GetTableName()!,
                    i.Properties.Select(p => p.GetColumnName()),
                    i.GetFilter(),
                    i.IsUnique),
                StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, string>> LoadDatabaseIndexesAsync()
    {
        await using var conn = await _fixture.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();

        // Expression indexes report attnum 0 and have no attribute row, so they drop out of the
        // aggregate and would read as a column mismatch; the model declares none today, and one
        // added later should fail here rather than pass unchecked.
        cmd.CommandText = """
            SELECT c.relname,
                   t.relname,
                   (SELECT string_agg(a.attname, ',' ORDER BY k.ord)
                    FROM unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord)
                    JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum),
                   COALESCE(pg_get_expr(i.indpred, i.indrelid), ''),
                   i.indisunique
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indexrelid
            JOIN pg_class t ON t.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND i.indisvalid;
            """;

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            found[reader.GetString(0)] = Shape(
                reader.GetString(1),
                reader.IsDBNull(2) ? [] : reader.GetString(2).Split(','),
                reader.GetString(3),
                reader.GetBoolean(4));
        }

        return found;
    }

    /// <summary>
    /// One comparable string per index. Uniqueness is in the comparison because it is a
    /// correctness property rather than a performance one — a raw-SQL migration that rebuilt
    /// ix_sensor_glucose_tenant_source_sync_id without it would silently stop enforcing the
    /// connector dedup invariant.
    /// </summary>
    private static string Shape(string table, IEnumerable<string> columns, string? filter, bool unique) =>
        $"{(unique ? "unique " : string.Empty)}{table}({string.Join(',', columns)}) "
        + $"where {NormalisePredicate(filter)}";

    /// <summary>
    /// Postgres re-renders a predicate on the way in: it parenthesises, quotes identifiers it
    /// chose to quote, and makes literal casts explicit (<c>status = 'pending'</c> comes back as
    /// <c>status::text = 'pending'::text</c>). Strips all three so only a predicate that is
    /// genuinely different, or missing, reads as a mismatch.
    /// </summary>
    private static string NormalisePredicate(string? filter)
    {
        var stripped = Regex.Replace(filter ?? string.Empty, "::[a-z ]+", string.Empty);

        return new string(stripped
            .Where(ch => !char.IsWhiteSpace(ch) && ch is not ('(' or ')' or '"'))
            .ToArray())
            .ToLowerInvariant();
    }
}
