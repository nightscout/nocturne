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

    // Table and scope identifiers come from the model and OAuthScopes constants, never user
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
    /// <c>app.visible_categories</c>. A table with no governing scope is hidden from shares
    /// entirely. The policy is FOR SELECT only, so writes (background ingest) are unaffected.
    /// </summary>
    /// <param name="table">The snake_case table name.</param>
    /// <param name="governingScope">The OAuth read scope that unlocks the table for a share,
    /// or <c>null</c> when the table is hidden from shares.</param>
    public static string BuildPolicySql(string table, string? governingScope)
    {
        if (!TableNamePattern.IsMatch(table))
            throw new ArgumentException($"Unsafe table identifier '{table}'.", nameof(table));
        if (governingScope is not null && !ScopePattern.IsMatch(governingScope))
            throw new ArgumentException($"Unsafe scope identifier '{governingScope}'.", nameof(governingScope));

        var usingExpr = "current_setting('app.is_share', true) IS DISTINCT FROM 'true'";
        if (governingScope is not null)
        {
            usingExpr +=
                $" OR '{governingScope}' = ANY(string_to_array(current_setting('app.visible_categories', true), ','))";
        }

        return $"""
            ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS {PolicyName} ON {table};
            CREATE POLICY {PolicyName} ON {table} AS RESTRICTIVE FOR SELECT USING ({usingExpr});
            """;
    }
}
