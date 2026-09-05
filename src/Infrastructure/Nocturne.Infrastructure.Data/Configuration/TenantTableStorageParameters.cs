namespace Nocturne.Infrastructure.Data.Configuration;

/// <summary>
/// The storage parameters Nocturne pins on every tenant-scoped table, and the SQL that reads and
/// writes them. Applied at startup by
/// <see cref="Extensions.DatabaseInitializationExtensions.ReconcileTenantTableStorageParametersAsync"/>.
/// </summary>
/// <remarks>
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
/// table. The value is a ceiling: a lower value an operator has set on a table is kept, a higher
/// or absent one is replaced.
/// </remarks>
public static class TenantTableStorageParameters
{
    /// <summary>The storage parameter pinned on every tenant-scoped table.</summary>
    public const string AnalyzeScaleFactorName = "autovacuum_analyze_scale_factor";

    /// <summary>The ceiling applied to <see cref="AnalyzeScaleFactorName"/>.</summary>
    public const string AnalyzeScaleFactor = "0.01";

    /// <summary>
    /// Selects, from the table names in <c>$1</c>, those whose stored
    /// <see cref="AnalyzeScaleFactorName"/> (parameter <c>$2</c>) is absent or numerically above
    /// <c>$3</c>. Only these need DDL, so a steady-state startup issues none. PostgreSQL stores the
    /// option as the text it was given, so the comparison casts rather than matching text.
    /// </summary>
    public const string DriftQuerySql =
        """
        SELECT c.relname::text
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN LATERAL (
            SELECT o.option_value
            FROM pg_options_to_table(c.reloptions) o
            WHERE o.option_name = $2) stored ON true
        WHERE n.nspname = 'public'
          AND c.relkind = 'r'
          AND c.relname::text = ANY($1)
          AND (stored.option_value IS NULL OR stored.option_value::numeric > $3::numeric)
        ORDER BY c.relname
        """;

    /// <summary>
    /// Sets <see cref="AnalyzeScaleFactorName"/> on <paramref name="table"/>. <c>ALTER TABLE … SET</c>
    /// takes a <c>SHARE UPDATE EXCLUSIVE</c> lock, which reads and writes do not conflict with but an
    /// autovacuum of the same table does, so the statement runs under a 3 s <c>lock_timeout</c>
    /// scoped to its transaction rather than queue behind one.
    /// </summary>
    /// <param name="table">The snake_case table name.</param>
    public static string BuildSetSql(string table)
    {
        SqlIdentifier.Require(table, nameof(table));
        return $"""
            SET LOCAL lock_timeout = '3s';
            ALTER TABLE {table} SET ({AnalyzeScaleFactorName} = {AnalyzeScaleFactor});
            """;
    }
}
