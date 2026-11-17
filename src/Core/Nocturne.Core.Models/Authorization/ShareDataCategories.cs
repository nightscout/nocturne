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
            [Scope.AlertsRead] = new[]
            {
                // What /alerts/active reads: the open excursion, its instances, and the rule for its
                // name and severity. Rule channels stay behind DenyNoAccountViewer in the API, and
                // mutes, deliveries, DND windows and sounds stay hidden.
                "alert_excursions",
                "alert_instances",
                "alert_rules",
                // A share records and reads back its own "seen"; the API filters it to the caller.
                "alert_excursion_receipts",
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
            ["alert_rules"] = null,
            ["alert_excursion_receipts"] = "seen_at",
            ["connector_food_entries"] = "consumed_at",
        };

    /// <summary>
    /// End column per governed table of spans, clamped by overlap like
    /// <see cref="HiddenSpanEndColumns"/> rather than by a recency column: an alert still open stays in
    /// front of a share without full history, and of a clamped member, however long ago it started.
    /// A governed table is in exactly one of this and <see cref="RecencyColumns"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> GovernedSpanEndColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["alert_excursions"] = "ended_at",
            ["alert_instances"] = "resolved_at",
        };

    /// <summary>
    /// Recency column per table that no scope governs, so it stays hidden from every share, but whose
    /// rows a history-clamped member still reads only the last 24 hours of. <c>notes</c> holds the
    /// Note and Announcement treatments the legacy treatment reads serve;
    /// <c>compression_low_suggestions</c> holds glucose-derived regions (nadir, drop rate) of past
    /// nights. A <c>discrepancy_details</c> row is recorded with its analysis, so the two clamp together.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> HiddenRecencyColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["notes"] = "timestamp",
            ["compression_low_suggestions"] = "end_mills",
            ["body_weights"] = "mills",
            ["lab_hba1c_results"] = "measured_at",
            ["system_events"] = "mills",
            ["device_clock_observations"] = "observed_at",
            ["discrepancy_analyses"] = "analysis_timestamp",
            ["discrepancy_details"] = "recorded_at",
        };

    /// <summary>
    /// Tables whose recency column holds Unix milliseconds rather than a timestamp, so the clamp
    /// compares it with the window's start in milliseconds.
    /// </summary>
    public static readonly IReadOnlySet<string> UnixMillisRecencyTables =
        new HashSet<string>(StringComparer.Ordinal) { "compression_low_suggestions", "body_weights", "system_events" };

    /// <summary>
    /// End column per hidden table of spans, which a history-clamped member reads by overlap with the
    /// last 24 hours: a span still running (end null) or ended inside the window stays visible however
    /// long ago it started. <c>profile_activations</c> and <c>state_spans</c> hold the profile
    /// activations, pump modes and overrides the therapy resolvers read to find what is active now, so
    /// clamping them by start would leave a clamped member's carb ratio, basal, sensitivity and targets on
    /// the default profile. They also hold the profile switches, overrides and temporary targets the
    /// legacy treatment reads serve.
    /// </summary>
    /// <remarks>
    /// The same rule, with <see cref="GovernedSpanEndColumns"/> for the alert tables, keeps an open
    /// alert, an active tracker (<c>tracker_instances</c> not completed) and a notification still
    /// in its recipient's inbox (<c>in_app_notifications</c> not archived, within
    /// <see cref="HiddenOpenSpanBounds"/>) in front of a clamped member, and keeps a sleep session that
    /// ended inside the window whole however early it began. A row that never ends and has no bound
    /// stays visible for good: a tracker nobody completes, or an excursion the alert engine never
    /// closed. A clamped request has no cache partition, so no cache serves it these rows; see
    /// <c>IReaderCachePartition</c>.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> HiddenSpanEndColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["state_spans"] = "end_timestamp",
            ["profile_activations"] = "end_timestamp",
            ["sleep_sessions"] = "end_time",
            ["tracker_instances"] = "completed_at",
            ["in_app_notifications"] = "archived_at",
        };

    /// <summary>
    /// How long a still-open span of a <see cref="HiddenSpanEndColumns"/> table stays visible to a
    /// clamped member, counted from its start column; one that ended inside the last 24 hours stays
    /// visible regardless. An <c>in_app_notifications</c> row embeds glucose-derived text and can sit
    /// unarchived for months, so an open one is shown for 7 days from when it was raised: long enough
    /// for an alert or tracker nudge still awaiting action across a week. Nothing active is lost past
    /// it: an alert's notifications are archived when its excursion closes, and an open alert or
    /// tracker itself stays visible on its own table however old.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, OpenSpanBound> HiddenOpenSpanBounds =
        new Dictionary<string, OpenSpanBound>(StringComparer.Ordinal)
        {
            ["in_app_notifications"] = new("created_at", 7),
        };

    /// <summary>
    /// Hidden tables whose rows are parts of a parent row, which a history-clamped member reads exactly
    /// while the clamp shows the parent: by the parent's column in <see cref="RecencyColumns"/> or
    /// <see cref="HiddenSpanEndColumns"/>. A <c>treatment_foods</c> line is part of its carb intake and
    /// follows an edit to the intake's time with no copy to keep in step; the stages and samples of a
    /// sleep session stay with the session, so a night shown is shown whole. A mute or a delivery of an
    /// open alert stays visible however old it is: clamped by its own time, a mute would lapse and the
    /// member would be paged again, and a pending delivery would drop out of the bot's queue.
    /// </summary>
    /// <remarks>
    /// The policy tests the clamp before the <c>EXISTS</c>, so only a clamped reader runs it, as one
    /// primary-key probe of the parent per row. Children are read by their indexed foreign key, so a
    /// night's few thousand biometric samples probe the same parent row and page.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, ParentRecency> HiddenParentRecency =
        new Dictionary<string, ParentRecency>(StringComparer.Ordinal)
        {
            ["treatment_foods"] = new("carb_intakes", "carb_intake_id"),
            ["sleep_stages"] = new("sleep_sessions", "sleep_session_id"),
            ["sleep_biometric_samples"] = new("sleep_sessions", "sleep_session_id"),
            ["alert_excursion_mutes"] = new("alert_excursions", "alert_excursion_id"),
            ["alert_deliveries"] = new("alert_instances", "alert_instance_id"),
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

    /// <summary>Whether the <see cref="RecencyColumnFor"/> column of <paramref name="table"/> holds Unix milliseconds.</summary>
    public static bool RecencyIsUnixMillis(string table) => UnixMillisRecencyTables.Contains(table);

    /// <summary>
    /// Returns the end column a span table is clamped by overlap on, or <c>null</c> when the table is
    /// not a clamped span table. See <see cref="HiddenSpanEndColumns"/>.
    /// </summary>
    public static string? SpanEndColumnFor(string table) =>
        GovernedSpanEndColumns.TryGetValue(table, out var column) ? column
        : HiddenSpanEndColumns.GetValueOrDefault(table);

    /// <summary>
    /// Returns the parent a table is clamped through, or <c>null</c> when it is not. See
    /// <see cref="HiddenParentRecency"/>.
    /// </summary>
    public static ParentRecency? ParentRecencyFor(string table) => HiddenParentRecency.GetValueOrDefault(table);

    /// <summary>
    /// Returns the bound on how long an open span of <paramref name="table"/> stays visible, or
    /// <c>null</c> when it has none. See <see cref="HiddenOpenSpanBounds"/>.
    /// </summary>
    public static OpenSpanBound? OpenSpanBoundFor(string table) => HiddenOpenSpanBounds.GetValueOrDefault(table);

    /// <summary>
    /// Every table hidden from shares whose rows a history-clamped member reads only inside the window:
    /// the keys of <see cref="HiddenRecencyColumns"/>, <see cref="HiddenSpanEndColumns"/> and
    /// <see cref="HiddenParentRecency"/>.
    /// </summary>
    public static IEnumerable<string> ClampedHiddenTables =>
        HiddenRecencyColumns.Keys.Concat(HiddenSpanEndColumns.Keys).Concat(HiddenParentRecency.Keys);

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
                if (HiddenRecencyColumns.ContainsKey(table) || HiddenSpanEndColumns.ContainsKey(table)
                    || HiddenParentRecency.ContainsKey(table))
                {
                    throw new InvalidOperationException(
                        $"Governed table '{table}' is also in {nameof(HiddenRecencyColumns)}, {nameof(HiddenSpanEndColumns)} or {nameof(HiddenParentRecency)}; declare it in {nameof(RecencyColumns)} or {nameof(GovernedSpanEndColumns)} only.");
                }
                if (RecencyColumns.ContainsKey(table) == GovernedSpanEndColumns.ContainsKey(table))
                {
                    throw new InvalidOperationException(
                        $"Governed table '{table}' needs exactly one entry in {nameof(RecencyColumns)} or " +
                        $"{nameof(GovernedSpanEndColumns)}. Declare its recency column, its span end column, " +
                        "or an explicit null recency to exempt it from the share 24-hour clamp.");
                }
            }
        }

        foreach (var table in GovernedSpanEndColumns.Keys)
        {
            if (!map.ContainsKey(table))
            {
                throw new InvalidOperationException(
                    $"'{table}' is in {nameof(GovernedSpanEndColumns)} but no scope governs it; use {nameof(HiddenSpanEndColumns)}.");
            }
        }

        foreach (var (child, parent) in HiddenParentRecency)
        {
            if (HiddenRecencyColumns.ContainsKey(child) || HiddenSpanEndColumns.ContainsKey(child))
            {
                throw new InvalidOperationException(
                    $"Table '{child}' is clamped through its parent, so it cannot also have a clamp column of its own.");
            }
            var parentHasTimestamp = RecencyColumns.GetValueOrDefault(parent.Table) is not null
                || SpanEndColumnFor(parent.Table) is not null;
            if (!parentHasTimestamp || UnixMillisRecencyTables.Contains(parent.Table))
            {
                throw new InvalidOperationException(
                    $"Parent '{parent.Table}' of '{child}' needs a timestamp column in {nameof(RecencyColumns)} or {nameof(HiddenSpanEndColumns)}.");
            }
        }

        foreach (var (table, bound) in HiddenOpenSpanBounds)
        {
            if (!HiddenSpanEndColumns.ContainsKey(table) || bound.MaxOpenDays <= 0)
            {
                throw new InvalidOperationException(
                    $"Open-span bound on '{table}' needs a span end column in {nameof(HiddenSpanEndColumns)} and a positive day count.");
            }
            if (HiddenParentRecency.Values.Any(p => p.Table == table))
            {
                throw new InvalidOperationException(
                    $"'{table}' is a parent in {nameof(HiddenParentRecency)}, whose clamp does not apply an open-span bound.");
            }
        }

        return map;
    }
}

/// <summary>
/// How long an open span stays visible to a clamped member: <paramref name="MaxOpenDays"/> days from
/// its <paramref name="StartColumn"/>.
/// </summary>
public sealed record OpenSpanBound(string StartColumn, int MaxOpenDays);

/// <summary>
/// The parent row a hidden table is clamped through: <paramref name="Table"/> is the parent table and
/// <paramref name="ForeignKeyColumn"/> the child's column holding the parent's <c>id</c>.
/// </summary>
public sealed record ParentRecency(string Table, string ForeignKeyColumn);
