using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Security;

/// <summary>
/// Builds the per-category public-share Row-Level-Security policy applied to every
/// tenant-scoped table, and resolves the tenant-scoped table set from the EF model.
/// The startup reconciler applies these policies and a guard test asserts the same set
/// is fully classified, so the C# category map (<c>ShareDataCategories</c>) and the live
/// database policies cannot drift.
/// </summary>
public static class ShareRlsPolicy
{
    /// <summary>Name of the RESTRICTIVE FOR SELECT policy applied to every tenant-scoped table.</summary>
    public const string PolicyName = "share_category_read";

    // Table and scope identifiers come from the model and Scope constants, never user
    // input; the patterns are belt-and-suspenders so a malformed identifier fails closed
    // (throws) rather than being interpolated into DDL.
    private static readonly Regex TableNamePattern = new("^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex ScopePattern = new(@"^[a-z]+\.[a-z]+$", RegexOptions.Compiled);

    /// <summary>
    /// Distinct table names of every <see cref="ITenantScoped"/> entity in the model,
    /// resolved from EF's relational mapping so a table named via <c>ToTable()</c> or a
    /// <c>[Table]</c> attribute is found either way. Ordinal-sorted for deterministic output.
    /// </summary>
    public static IReadOnlyList<string> TenantScopedTableNames(IModel model) =>
        model.GetEntityTypes()
            .Where(t => typeof(ITenantScoped).IsAssignableFrom(t.ClrType))
            .Select(t => t.GetTableName())
            .Where(n => n is not null)
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Idempotent DDL that enables RLS on the table and (re)creates the share-category policy.
    /// A non-share connection (<c>app.is_share</c> ≠ 'true') is unaffected; a public share sees
    /// the table's rows only when <paramref name="governingScope"/> is present in
    /// <c>app.visible_categories</c> — and, when <paramref name="recencyColumn"/> is given, only
    /// rows from the last 24 hours unless <c>app.share_full_history</c> is 'true' (fail-closed:
    /// a share connection that never sets the GUC gets the clamp). A table with no governing
    /// scope is hidden from shares entirely. The policy is FOR SELECT only, so writes
    /// (background ingest) are unaffected.
    /// </summary>
    /// <param name="table">The snake_case table name.</param>
    /// <param name="governingScope">The OAuth read scope that unlocks the table for a share,
    /// or <c>null</c> when the table is hidden from shares.</param>
    /// <param name="recencyColumn">The timestamp column the 24-hour clamp applies to, or
    /// <c>null</c> when the table is exempt (catalog data with no per-row time).</param>
    public static string BuildPolicySql(string table, string? governingScope, string? recencyColumn = null)
    {
        if (!TableNamePattern.IsMatch(table))
            throw new ArgumentException($"Unsafe table identifier '{table}'.", nameof(table));
        if (governingScope is not null && !ScopePattern.IsMatch(governingScope))
            throw new ArgumentException($"Unsafe scope identifier '{governingScope}'.", nameof(governingScope));
        if (recencyColumn is not null && !TableNamePattern.IsMatch(recencyColumn))
            throw new ArgumentException($"Unsafe column identifier '{recencyColumn}'.", nameof(recencyColumn));

        var usingExpr = "current_setting('app.is_share', true) IS DISTINCT FROM 'true'";
        if (governingScope is not null)
        {
            var shareExpr =
                $"'{governingScope}' = ANY(string_to_array(current_setting('app.visible_categories', true), ','))";
            if (recencyColumn is not null)
            {
                // "timestamp" is quoted: it is a type keyword in PostgreSQL.
                shareExpr =
                    $"({shareExpr} AND (current_setting('app.share_full_history', true) = 'true'" +
                    $" OR \"{recencyColumn}\" >= now() - interval '24 hours'))";
            }

            usingExpr += $" OR {shareExpr}";
        }

        return $"""
            ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS {PolicyName} ON {table};
            CREATE POLICY {PolicyName} ON {table} AS RESTRICTIVE FOR SELECT USING ({usingExpr});
            """;
    }
}
