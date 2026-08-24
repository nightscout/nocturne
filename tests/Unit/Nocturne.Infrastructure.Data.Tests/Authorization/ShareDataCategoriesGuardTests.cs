using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Security;

namespace Nocturne.Infrastructure.Data.Tests.Authorization;

/// <summary>
/// Forces every tenant-scoped table to be classified for public-share visibility.
/// A new <see cref="ITenantScoped"/> entity fails the build until its table is put
/// in <see cref="ShareDataCategories.GovernedTables"/> (shareable) or in
/// <see cref="KnownHiddenTables"/> (deliberately hidden) — so a PHI table can never
/// reach a share by being forgotten.
/// </summary>
[Trait("Category", "Unit")]
public class ShareDataCategoriesGuardTests
{
    /// <summary>
    /// Tenant-scoped tables intentionally not exposed to public shares: therapy and
    /// profile data, alert internals, audit logs, OAuth/auth state, connector config,
    /// trackers, and internal bookkeeping. None is governed by a publicly-shareable
    /// read scope.
    /// </summary>
    private static readonly IReadOnlySet<string> KnownHiddenTables = new HashSet<string>(StringComparer.Ordinal)
    {
        "alert_condition_timers", "alert_custom_sounds", "alert_deliveries", "alert_excursions",
        "alert_instances", "alert_invites", "alert_rule_channels", "alert_rules", "alert_tracker_state",
        "basal_schedules", "body_weights", "carb_ratio_schedules", "client_devices", "clock_faces", "coach_mark_states",
        "compression_low_suggestions", "connector_configurations", "data_source_metadata",
        "dedup_reconcile_state", "devices", "discrepancy_analyses",
        "discrepancy_details", "dnd_windows", "in_app_notifications", "linked_records", "member_invites",
        "membership_requests",
        "mutation_audit_log", "notes", "oauth_authorization_codes", "oauth_clients", "oauth_device_codes",
        "oauth_grants", "oauth_refresh_tokens", "patient_devices", "patient_insulins", "patient_records",
        "read_access_log", "sensitivity_schedules", "settings",
        "sleep_biometric_samples", "sleep_sessions", "sleep_stages",
        "state_spans", "system_events",
        "target_range_schedules", "tenant_alert_settings",
        "tenant_data_retention_config", "therapy_settings", "timezone_timeline", "tracker_definitions",
        "tracker_instances", "tracker_notification_thresholds", "tracker_presets", "treatment_foods",
        "user_food_favorites",
    };

    private static readonly Lazy<IReadOnlySet<string>> TenantScopedTables = new(() =>
    {
        using var context = OfflineDbContext.Create();
        return ShareRlsPolicy.TenantScopedTableNames(context.Model).ToHashSet(StringComparer.Ordinal);
    });

    [Fact]
    public void EveryTenantScopedTable_IsClassifiedShareableOrHidden()
    {
        var unclassified = TenantScopedTables.Value
            .Where(t => ShareDataCategories.GoverningScopeFor(t) is null && !KnownHiddenTables.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        unclassified.Should().BeEmpty(
            "every ITenantScoped table must be classified shareable (ShareDataCategories.GovernedTables) "
            + "or hidden (KnownHiddenTables)");
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
    public void KnownHiddenTables_AreAllStillTenantScoped()
    {
        var real = TenantScopedTables.Value;

        KnownHiddenTables.Where(t => !real.Contains(t))
            .Should().BeEmpty("KnownHiddenTables must not list a stale, non-ITenantScoped table");
    }
}
