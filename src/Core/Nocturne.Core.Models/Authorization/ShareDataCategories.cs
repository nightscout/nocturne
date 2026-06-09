namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Single source of truth mapping each publicly-shareable read scope to the
/// database tables it governs, for per-category Row-Level Security on public
/// share links. A table is visible to a share only when its governing scope is
/// present in the share's granted-category set; every <c>ITenantScoped</c> table
/// not listed here is hidden from shares (fail-safe default).
/// </summary>
/// <remarks>
/// The category vocabulary is the OAuth read scopes in <see cref="OAuthScopes"/>,
/// not a parallel taxonomy. The reconciler that applies the RLS policies and the
/// guard test that asserts full <c>ITenantScoped</c> coverage both read from this
/// type, so the C# map and the live database policies cannot drift.
/// </remarks>
public static class ShareDataCategories
{
    /// <summary>
    /// Governing read scope to the tables that scope unlocks for a share.
    /// Security-critical: placing a table under a scope makes its rows visible to
    /// any share granted that scope. Validated against the live <c>ITenantScoped</c>
    /// entity set by the coverage guard test.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> GovernedTables =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [OAuthScopes.GlucoseRead] = new[]
            {
                "sensor_glucose",
                "bg_checks",
                "meter_glucose",
                "calibrations",
            },
            [OAuthScopes.TreatmentsRead] = new[]
            {
                "boluses",
                "carb_intakes",
                "temp_basals",
                "basal_injections",
                "bolus_calculations",
            },
            [OAuthScopes.DevicesRead] = new[]
            {
                // The `devices` master registry is deliberately omitted (hidden) for v1:
                // no share-reachable endpoint reads it. Revisit if a share view needs it.
                "device_events",
                "device_status_extras",
                "pump_snapshots",
                "uploader_snapshots",
                "aps_snapshots",
            },
            [OAuthScopes.HeartRateRead] = new[] { "heart_rates" },
            [OAuthScopes.StepCountRead] = new[] { "step_counts" },
            [OAuthScopes.FoodRead] = new[]
            {
                // `treatment_foods` and `user_food_favorites` are deliberately hidden for
                // v1: the former ties food to treatments, the latter is a personal pick
                // list. Classify them explicitly if a share view should expose them.
                "foods",
                "connector_food_entries",
            },
        };

    private static readonly IReadOnlyDictionary<string, string> TableToScope = BuildTableToScope();

    /// <summary>The governing scopes that have at least one table (the shareable, table-backed categories).</summary>
    public static IReadOnlyCollection<string> GoverningScopes => (IReadOnlyCollection<string>)GovernedTables.Keys;

    /// <summary>
    /// Returns the governing read scope for a table, or <c>null</c> when the table
    /// is not share-categorized (hidden from shares).
    /// </summary>
    public static string? GoverningScopeFor(string table) =>
        TableToScope.TryGetValue(table, out var scope) ? scope : null;

    /// <summary>
    /// Computes the value for the <c>app.visible_categories</c> GUC carried by a
    /// share connection: the comma-separated governing scopes the share's granted
    /// scopes satisfy (ordinal-sorted, deterministic). Empty when the share unlocks
    /// no categorized data.
    /// </summary>
    public static string ComputeVisibleCategoriesCsv(IEnumerable<string> grantedScopes)
    {
        var granted = grantedScopes as ISet<string> ?? new HashSet<string>(grantedScopes, StringComparer.Ordinal);

        var visible = GovernedTables.Keys
            .Where(scope => OAuthScopes.SatisfiesScope(granted, scope))
            .OrderBy(scope => scope, StringComparer.Ordinal);

        return string.Join(",", visible);
    }

    private static Dictionary<string, string> BuildTableToScope()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (scope, tables) in GovernedTables)
        {
            foreach (var table in tables)
            {
                map.Add(table, scope); // throws on a duplicate table across scopes — a map authoring error
            }
        }

        return map;
    }
}
