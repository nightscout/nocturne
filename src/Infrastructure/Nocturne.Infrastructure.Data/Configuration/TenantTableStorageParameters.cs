namespace Nocturne.Infrastructure.Data.Configuration;

/// <summary>
/// The storage parameters Nocturne pins on every tenant-scoped table, and the SQL that reads and
/// writes them. Applied at startup by
/// <see cref="Extensions.DatabaseInitializationExtensions.ReconcileTenantTableStorageParametersAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL re-samples a table's planner statistics only after
/// <c>autovacuum_analyze_scale_factor</c> (default 0.1) of its rows change. On a multi-tenant
/// table of millions of rows that is days, and a tenant provisioned in between is invisible to
/// the planner: its <c>tenant_id</c> is in no most-common-values list, every scan filtered on it
/// is estimated at one row, and the plans chosen on that estimate are the wrong ones. At 0.01 a
/// tenant is analysed within an autovacuum nap of its rows exceeding about 1 % of the table,
/// which a connector backfill crosses early; a tenant below that waits for the same 1 % of
/// aggregate churn, and pays the mis-plan only over its own rows, since the tenant-leading
/// indexes confine the wrong scan to them. The analyze sample size is fixed
/// (300 × <c>default_statistics_target</c> rows), so the extra analyzes do not grow with the
/// table.
/// </para>
/// <para>
/// <c>autovacuum_vacuum_scale_factor</c> (default 0.2) has the same shape on the tables that are
/// upserted: a row rewritten every few minutes leaves a dead tuple behind, and the newest index
/// entries point at dead tuples until a vacuum reaps them, so every newest-first read walks past
/// them. At 0.2 a table of five million rows vacuums only after a million dead rows, days later
/// on a quiet table. At 0.02 the tails stay short. A vacuum's heap pass scales with the pages
/// churned since the last one (the visibility map skips the rest), but each pass also reads the
/// table's indexes in full, so the lower factor costs about ten times as many index passes. The
/// dead tails it keeps short sit on the path of every newest-first read.
/// </para>
/// <para>
/// Each value is a ceiling: a lower value an operator has set on a table is kept, a higher or
/// absent one is replaced.
/// </para>
/// </remarks>
public static class TenantTableStorageParameters
{
    /// <summary>A storage parameter and the value above which it is replaced.</summary>
    /// <param name="Name">The storage parameter name.</param>
    /// <param name="Ceiling">The ceiling, as the text written into the DDL.</param>
    public sealed record Parameter(string Name, string Ceiling);

    /// <summary>The planner statistics refresh threshold.</summary>
    public static readonly Parameter AnalyzeScaleFactor = new("autovacuum_analyze_scale_factor", "0.01");

    /// <summary>The dead-tuple vacuum threshold.</summary>
    public static readonly Parameter VacuumScaleFactor = new("autovacuum_vacuum_scale_factor", "0.02");

    /// <summary>Every parameter pinned on every tenant-scoped table, in the order it is set.</summary>
    public static readonly IReadOnlyList<Parameter> All = [AnalyzeScaleFactor, VacuumScaleFactor];

    /// <summary>
    /// Selects, from the table names in <c>$1</c>, each table where a parameter named in <c>$2</c>
    /// is absent or numerically above its ceiling in <c>$3</c> (the arrays pair by position),
    /// with the names of the parameters that drift. Only these need DDL, so a steady-state startup
    /// issues none. PostgreSQL stores the option as the text it was given, so the comparison casts
    /// rather than matching text.
    /// </summary>
    public const string DriftQuerySql =
        """
        SELECT c.relname::text, array_agg(p.name ORDER BY p.ord)
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        CROSS JOIN unnest($2::text[], $3::text[]) WITH ORDINALITY AS p(name, ceiling, ord)
        LEFT JOIN LATERAL (
            SELECT o.option_value
            FROM pg_options_to_table(c.reloptions) o
            WHERE o.option_name = p.name) stored ON true
        WHERE n.nspname = 'public'
          AND c.relkind = 'r'
          AND c.relname::text = ANY($1)
          AND (stored.option_value IS NULL OR stored.option_value::numeric > p.ceiling::numeric)
        GROUP BY c.relname
        ORDER BY c.relname
        """;

    /// <summary>
    /// Sets each of <paramref name="drifted"/> on <paramref name="table"/> in one statement.
    /// <c>ALTER TABLE … SET</c> takes a <c>SHARE UPDATE EXCLUSIVE</c> lock, which reads and writes
    /// do not conflict with but an autovacuum of the same table does, so the statement runs under a
    /// 3 s <c>lock_timeout</c> scoped to its transaction rather than queue behind one.
    /// </summary>
    /// <param name="table">The snake_case table name.</param>
    /// <param name="drifted">The parameters to set, which must be members of <see cref="All"/>.</param>
    public static string BuildSetSql(string table, IReadOnlyCollection<Parameter> drifted)
    {
        SqlIdentifier.Require(table, nameof(table));
        if (drifted.Count == 0 || drifted.Any(p => !All.Contains(p)))
            throw new ArgumentException("Parameters must be a non-empty subset of All.", nameof(drifted));

        var assignments = string.Join(", ", drifted.Select(p => $"{p.Name} = {p.Ceiling}"));
        return $"""
            SET LOCAL lock_timeout = '3s';
            ALTER TABLE {table} SET ({assignments});
            """;
    }
}
