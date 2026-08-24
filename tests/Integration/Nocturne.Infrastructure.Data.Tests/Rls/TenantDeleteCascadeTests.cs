using FluentAssertions;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// Guards the invariant that deleting a tenant row clears every tenant-scoped table.
/// Tenant deletion (<c>TenantService.DeleteAsync</c>) and the demo reset
/// (<c>DemoTenantService.ResetAsync</c>) both rely on the database's cascade rather
/// than a hand-maintained table list, so a new tenant-scoped entity whose foreign key
/// does not cascade would silently leave rows behind — orphaned for a delete, and
/// still visible to the next demo visitor after a reset.
/// </summary>
/// <remarks>
/// Reuses the seedless RLS fixture: this inspects schema metadata only.
/// </remarks>
[Collection("RLS completeness")]
[Trait("Category", "Integration")]
public class TenantDeleteCascadeTests
{
    private const string TenantsTable = "tenants";

    private readonly RlsCompletenessFixture _fixture;

    public TenantDeleteCascadeTests(RlsCompletenessFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task EveryTenantScopedTable_CascadesFromTenantDelete()
    {
        var tenantScoped = _fixture.TenantScopedTableNames;
        tenantScoped.Should().NotBeEmpty("the model must expose tenant-scoped entities");

        var cascadeEdges = await LoadCascadeEdgesAsync();
        var reachesTenants = TablesReaching(TenantsTable, cascadeEdges);

        var orphaned = tenantScoped
            .Where(table => !reachesTenants.Contains(table))
            .OrderBy(table => table, StringComparer.Ordinal)
            .ToList();

        orphaned.Should().BeEmpty(
            "every tenant-scoped table needs an ON DELETE CASCADE path to '{0}' so a tenant " +
            "delete or demo reset clears it; add the cascade in NocturneDbContext for: {1}",
            TenantsTable,
            string.Join(", ", orphaned));
    }

    /// <summary>
    /// Child-to-parent edges for foreign keys whose delete action is CASCADE
    /// (<c>confdeltype = 'c'</c>) and whose referencing columns are all NOT NULL.
    /// </summary>
    /// <remarks>
    /// A nullable referencing column is excluded deliberately: the cascade fires only
    /// for rows that actually point at a parent, so a row with a NULL foreign key
    /// survives the parent's deletion. Counting such an edge as a cascade path would
    /// let the guard pass while rows outlive the tenant.
    /// </remarks>
    private async Task<IReadOnlyList<(string Child, string Parent)>> LoadCascadeEdgesAsync()
    {
        await using var conn = await _fixture.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT child.relname, parent.relname
            FROM pg_constraint c
            JOIN pg_class child ON child.oid = c.conrelid
            JOIN pg_class parent ON parent.oid = c.confrelid
            WHERE c.contype = 'f'
              AND c.confdeltype = 'c'
              AND child.relnamespace = 'public'::regnamespace
              AND NOT EXISTS (
                  SELECT 1
                  FROM unnest(c.conkey) AS col(attnum)
                  JOIN pg_attribute a
                    ON a.attrelid = c.conrelid AND a.attnum = col.attnum
                  WHERE NOT a.attnotnull
              )
            """;

        var edges = new List<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            edges.Add((reader.GetString(0), reader.GetString(1)));
        }

        return edges;
    }

    /// <summary>
    /// Tables that reach <paramref name="root"/> by following cascade edges, i.e. the
    /// tables a delete of <paramref name="root"/> transitively clears.
    /// </summary>
    private static HashSet<string> TablesReaching(
        string root, IReadOnlyList<(string Child, string Parent)> cascadeEdges)
    {
        var childrenOf = cascadeEdges
            .GroupBy(e => e.Parent, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Child).ToList(), StringComparer.Ordinal);

        var reached = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([root]);

        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (!childrenOf.TryGetValue(parent, out var children))
                continue;

            foreach (var child in children.Where(c => reached.Add(c)))
            {
                queue.Enqueue(child);
            }
        }

        return reached;
    }
}
