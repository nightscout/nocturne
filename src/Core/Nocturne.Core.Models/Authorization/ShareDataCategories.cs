namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Single source of truth mapping each publicly-shareable read scope to the
/// database tables it governs, for per-category Row-Level Security on public
/// share links. A table is visible to a share only when its governing scope is
/// present in the share's granted-category set; every <c>ITenantScoped</c> table
/// not listed here is hidden from shares (fail-safe default).
/// </summary>
/// <remarks>
/// The category vocabulary is the OAuth read scopes in <see cref="Scope"/>,
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
            [Scope.GlucoseRead] = new[]
            {
                "sensor_glucose",
                "bg_checks",
                "meter_glucose",
                "calibrations",
            },
            [Scope.TreatmentsRead] = new[]
            {
                "boluses",
                "carb_intakes",
                "temp_basals",
                "basal_injections",
                "bolus_calculations",
            },
            [Scope.DevicesRead] = new[]
            {
                // The `devices` master registry is deliberately omitted (hidden) for v1:
                // no share-reachable endpoint reads it. Revisit if a share view needs it.
                "device_events",
                "device_status_extras",
                "pump_snapshots",
                "uploader_snapshots",
                "aps_snapshots",
            },
            [Scope.HeartRateRead] = new[] { "heart_rates" },
            [Scope.StepCountRead] = new[] { "step_counts" },
            [Scope.FoodRead] = new[]
            {
                // `treatment_foods` and `user_food_favorites` are deliberately hidden for
                // v1: the former ties food to treatments, the latter is a personal pick
                // list. Classify them explicitly if a share view should expose them.
                "foods",
                "connector_food_entries",
            },
        };

    /// <summary>
    /// Recency column per governed table, driving the share 24-hour clamp: a share without
    /// full history sees only rows whose recency column is within the last 24 hours. An
    /// explicit <c>null</c> marks a table as deliberately unclamped (catalog data with no
    /// per-row time, e.g. the food database). Every governed table must appear here — the
    /// type initializer throws on a missing entry, so a new governed table cannot silently
    /// skip the clamp decision.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> RecencyColumns =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["sensor_glucose"] = "timestamp",
            ["bg_checks"] = "timestamp",
            ["meter_glucose"] = "timestamp",
            ["calibrations"] = "timestamp",
            ["boluses"] = "timestamp",
            ["carb_intakes"] = "timestamp",
            ["temp_basals"] = "start_timestamp",
            ["basal_injections"] = "timestamp",
            ["bolus_calculations"] = "timestamp",
            ["device_events"] = "timestamp",
            ["device_status_extras"] = "timestamp",
            ["pump_snapshots"] = "timestamp",
            ["uploader_snapshots"] = "timestamp",
            ["aps_snapshots"] = "timestamp",
            ["heart_rates"] = "timestamp",
            ["step_counts"] = "timestamp",
            ["foods"] = null,
            ["connector_food_entries"] = "consumed_at",
        };

    /// <summary>
    /// Recency column per table that no scope governs, so it stays hidden from every share, but whose
    /// rows a history-clamped member still reads only the last 24 hours of. <c>notes</c> holds the
    /// Note and Announcement treatments the legacy treatment reads serve.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> HiddenRecencyColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["notes"] = "timestamp",
        };

    /// <summary>
    /// End column per hidden table of spans, which a history-clamped member reads by overlap with the
    /// last 24 hours: a span still running (end null) or ended inside the window stays visible however
    /// long ago it started. <c>state_spans</c> holds the profile switches, pump modes and overrides the
    /// therapy resolvers read to find what is active now, so clamping it by start would leave a clamped
    /// member's carb ratio, basal, sensitivity and targets on the default profile. It also holds the
    /// overrides, temporary targets and profile switches the legacy treatment reads serve.
    /// </summary>
    /// <remarks>Every tenant-keyed cache over these tables bypasses a clamped request.</remarks>
    public static readonly IReadOnlyDictionary<string, string> HiddenSpanEndColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["state_spans"] = "end_timestamp",
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
    /// Returns the recency column the 24-hour clamp applies to a table, or <c>null</c> when the
    /// table is deliberately unclamped or not classified for it.
    /// </summary>
    public static string? RecencyColumnFor(string table) =>
        RecencyColumns.TryGetValue(table, out var column) ? column
        : HiddenRecencyColumns.GetValueOrDefault(table);

    /// <summary>
    /// Returns the end column a span table is clamped by overlap on, or <c>null</c> when the table is
    /// not a clamped span table. See <see cref="HiddenSpanEndColumns"/>.
    /// </summary>
    public static string? SpanEndColumnFor(string table) => HiddenSpanEndColumns.GetValueOrDefault(table);

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
            .Where(scope => Scope.Satisfies(granted, scope))
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
                if (HiddenRecencyColumns.ContainsKey(table) || HiddenSpanEndColumns.ContainsKey(table))
                {
                    throw new InvalidOperationException(
                        $"Governed table '{table}' is also in {nameof(HiddenRecencyColumns)} or {nameof(HiddenSpanEndColumns)}; declare it in {nameof(RecencyColumns)} only.");
                }
                if (!RecencyColumns.ContainsKey(table))
                {
                    throw new InvalidOperationException(
                        $"Governed table '{table}' has no entry in {nameof(RecencyColumns)}. " +
                        "Declare its recency column, or an explicit null to exempt it from the share 24-hour clamp.");
                }
            }
        }

        return map;
    }
}
