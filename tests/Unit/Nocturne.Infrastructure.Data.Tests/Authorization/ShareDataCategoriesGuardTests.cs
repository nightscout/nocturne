using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using Nocturne.Core.Models.Authorization;

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
        "basal_schedules", "body_weights", "carb_ratio_schedules", "clock_faces", "coach_mark_states",
        "compression_low_suggestions", "connector_configurations", "data_source_metadata",
        "decomposition_batches", "dedup_reconcile_state", "devices", "discrepancy_analyses",
        "discrepancy_details", "in_app_notifications", "linked_records", "membership_requests",
        "mutation_audit_log", "notes", "oauth_authorization_codes", "oauth_clients", "oauth_device_codes",
        "oauth_grants", "oauth_refresh_tokens", "patient_devices", "patient_insulins", "patient_records",
        "read_access_log", "sensitivity_schedules", "settings", "state_spans", "system_events",
        "target_range_schedules", "tenant_alert_settings",
        "tenant_data_retention_config", "therapy_settings", "tracker_definitions", "tracker_instances",
        "tracker_notification_thresholds", "tracker_presets", "treatment_foods", "user_food_favorites",
    };

    private static IReadOnlyList<Type> TenantScopedEntities() =>
        typeof(ITenantScoped).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITenantScoped).IsAssignableFrom(t))
            .ToList();

    private static string? TableName(Type entity) => entity.GetCustomAttribute<TableAttribute>()?.Name;

    [Fact]
    public void EveryTenantScopedEntity_HasATableAttribute()
    {
        // The coverage guard resolves table names from [Table] attributes; an entity
        // without one would be silently skipped and therefore ungated.
        var untabled = TenantScopedEntities()
            .Where(t => TableName(t) is null)
            .Select(t => t.Name)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        untabled.Should().BeEmpty();
    }

    [Fact]
    public void EveryTenantScopedTable_IsClassifiedShareableOrHidden()
    {
        var unclassified = TenantScopedEntities()
            .Select(t => new { Entity = t.Name, Table = TableName(t) })
            .Where(x => x.Table is not null)
            .Where(x => ShareDataCategories.GoverningScopeFor(x.Table!) is null
                     && !KnownHiddenTables.Contains(x.Table!))
            .Select(x => $"{x.Entity} -> {x.Table}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        unclassified.Should().BeEmpty(
            "every ITenantScoped table must be classified shareable (ShareDataCategories.GovernedTables) "
            + "or hidden (KnownHiddenTables)");
    }

    [Fact]
    public void GovernedTables_OnlyReferenceRealTenantScopedTables()
    {
        var real = TenantScopedEntities().Select(TableName).Where(n => n is not null).ToHashSet(StringComparer.Ordinal);

        ShareDataCategories.GovernedTables.Values.SelectMany(t => t)
            .Where(t => !real.Contains(t))
            .Should().BeEmpty("GovernedTables must not reference a non-ITenantScoped table");
    }

    [Fact]
    public void KnownHiddenTables_AreAllStillTenantScoped()
    {
        var real = TenantScopedEntities().Select(TableName).Where(n => n is not null).ToHashSet(StringComparer.Ordinal);

        KnownHiddenTables.Where(t => !real.Contains(t))
            .Should().BeEmpty("KnownHiddenTables must not list a stale, non-ITenantScoped table");
    }
}
