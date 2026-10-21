using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Security;

namespace Nocturne.Infrastructure.Data.Tests.Authorization;

/// <summary>
/// Forces every tenant-scoped table to be classified for public-share visibility and for the
/// member 24-hour clamp. A new <see cref="ITenantScoped"/> entity fails the build until its table is
/// put in <see cref="ShareDataCategories.GovernedTables"/> (shareable), given a clamp in
/// <see cref="ShareDataCategories"/> (hidden, clamped), or listed in <see cref="UnclampedHiddenTables"/>
/// (hidden, deliberately unclamped), so a PHI table can never reach a share, or a history-limited
/// member, by being forgotten.
/// </summary>
[Trait("Category", "Unit")]
public class ShareDataCategoriesGuardTests
{
    /// <summary>
    /// Tenant-scoped tables hidden from public shares whose rows a history-clamped member reads in
    /// full. None is governed by a publicly-shareable read scope.
    /// </summary>
    private static readonly IReadOnlySet<string> UnclampedHiddenTables = new HashSet<string>(StringComparer.Ordinal)
    {
        // Configuration that applies to all of history rather than to rows of it: therapy and
        // profile schedules, alert and tracker setup, devices and connector settings.
        "alert_condition_timers", "alert_custom_sounds", "alert_rule_channels", "alert_rules", "alert_tracker_state",
        "basal_schedules", "carb_ratio_schedules", "client_devices", "clock_faces", "coach_mark_states",
        "connector_configurations", "data_source_metadata", "devices", "dnd_windows", "patient_devices",
        "patient_insulins", "patient_records", "sensitivity_schedules", "settings", "target_range_schedules",
        "tenant_alert_settings", "tenant_data_retention_config", "therapy_settings", "timezone_timeline",
        "tracker_definitions", "tracker_notification_thresholds", "tracker_presets", "translation_drafts",
        "user_food_favorites",

        // Access control and audit, read under their own permissions.
        "alert_invites", "login_codes", "member_invites", "membership_requests", "mutation_audit_log",
        "oauth_authorization_codes", "oauth_clients", "oauth_device_codes", "oauth_grants", "oauth_refresh_tokens",
        "read_access_log",

        // Internal bookkeeping.
        "dedup_reconcile_state",

        // Time-series whose member clamp is not yet decided.
        "alert_deliveries", "alert_excursion_mutes", "discrepancy_analyses", "discrepancy_details",
        "linked_records",

        // Owner/tenant.settings-only endpoint; history-clamped callers are refused.
        "rejected_uploads",
    };

    private static readonly Lazy<IReadOnlySet<string>> TenantScopedTables = new(() =>
    {
        using var context = OfflineDbContext.Create();
        return ShareRlsPolicy.TenantScopedTableNames(context.Model).ToHashSet(StringComparer.Ordinal);
    });

    private static bool IsClampedHidden(string table) => ShareDataCategories.ClampedHiddenTables.Contains(table);

    [Fact]
    public void EveryTenantScopedTable_IsClassifiedShareableClampedOrUnclamped()
    {
        var unclassified = TenantScopedTables.Value
            .Where(t => ShareDataCategories.GoverningScopeFor(t) is null
                && !IsClampedHidden(t)
                && !UnclampedHiddenTables.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        unclassified.Should().BeEmpty(
            "every ITenantScoped table must be shareable (ShareDataCategories.GovernedTables), hidden and "
            + "clamped for history-limited members (HiddenRecencyColumns, HiddenSpanEndColumns, "
            + "HiddenParentRecency), or hidden and deliberately unclamped (UnclampedHiddenTables)");
    }

    [Fact]
    public void GovernedTables_OnlyReferenceRealTenantScopedTables()
    {
        var real = TenantScopedTables.Value;

        ShareDataCategories.GovernedTables.Values.SelectMany(t => t)
            .Where(t => !real.Contains(t))
            .Should().BeEmpty("GovernedTables must not reference a non-ITenantScoped table");
    }

    [Fact]
    public void ClampedHiddenTables_AreAllStillTenantScoped()
    {
        var real = TenantScopedTables.Value;

        ShareDataCategories.ClampedHiddenTables
            .Where(t => !real.Contains(t))
            .Should().BeEmpty("a clamp must not name a stale, non-ITenantScoped table");
    }

    [Fact]
    public void UnclampedHiddenTables_AreNotAlsoClamped()
    {
        UnclampedHiddenTables.Where(IsClampedHidden)
            .Should().BeEmpty("a clamped table listed as unclamped would mislead whoever reads the list");
    }

    [Fact]
    public void UnclampedHiddenTables_AreAllStillTenantScopedAndUngoverned()
    {
        var real = TenantScopedTables.Value;

        UnclampedHiddenTables.Where(t => !real.Contains(t) || ShareDataCategories.GoverningScopeFor(t) is not null)
            .Should().BeEmpty("UnclampedHiddenTables must list only hidden, ITenantScoped tables");
    }
}
