using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Monitoring;

/// <summary>
/// Synthesises one managed alert rule per tracker notification threshold so tracker
/// notifications ride the alert engine's full pipeline (channels, DND, acknowledgement,
/// device actuation, replay) instead of the removed client-poll path.
/// </summary>
public interface ITrackerAlertRuleSyncService
{
    /// <summary>
    /// Reconciles the managed alert rules for <paramref name="definitionId"/> against its
    /// current notification thresholds: creates rules for new thresholds, overwrites the
    /// sync-owned fields (name, description, condition, severity, scope class) on existing
    /// ones, and deletes rules whose threshold is gone. User-editable fields (channels,
    /// client configuration, enabled state, DND opt-out, sort order) survive re-syncs.
    /// </summary>
    Task SyncDefinitionAsync(Guid definitionId, CancellationToken ct = default);

    /// <summary>
    /// Deletes every managed alert rule owned by <paramref name="definitionId"/>. Called
    /// when the tracker definition itself is deleted.
    /// </summary>
    Task DeleteRulesForDefinitionAsync(Guid definitionId, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class TrackerAlertRuleSyncService : ITrackerAlertRuleSyncService
{
    private readonly ITenantDbContextFactory _contextFactory;
    private readonly IRuleScopeClassifier _scopeClassifier;
    private readonly IAlertReferenceService _referenceService;
    private readonly ILogger<TrackerAlertRuleSyncService> _logger;

    /// <summary>Initialises a new <see cref="TrackerAlertRuleSyncService"/>.</summary>
    public TrackerAlertRuleSyncService(
        ITenantDbContextFactory contextFactory,
        IRuleScopeClassifier scopeClassifier,
        IAlertReferenceService referenceService,
        ILogger<TrackerAlertRuleSyncService> logger)
    {
        _contextFactory = contextFactory;
        _scopeClassifier = scopeClassifier;
        _referenceService = referenceService;
        _logger = logger;
    }

    /// <summary>The <see cref="AlertRuleEntity.ManagedBy"/> tag for a tracker definition's rules.</summary>
    public static string ManagedByTag(Guid definitionId) => $"tracker:{definitionId}";

    /// <inheritdoc />
    public async Task SyncDefinitionAsync(Guid definitionId, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateAsync(ct);
        var tenantId = db.TenantId;

        var definition = await db.TrackerDefinitions
            .Include(d => d.NotificationThresholds)
            .FirstOrDefaultAsync(d => d.Id == definitionId, ct);
        if (definition is null)
        {
            _logger.LogWarning("Tracker definition {DefinitionId} not found; skipping rule sync", definitionId);
            return;
        }

        var tag = ManagedByTag(definitionId);
        var managedRules = await db.AlertRules
            .Where(r => r.ManagedBy == tag)
            .ToDictionaryAsync(r => r.Id, ct);

        // The threshold editor replaces the whole threshold list (new row ids), which
        // clears the AlertRuleId links. Adopt orphaned managed rules so a threshold keeps
        // its rule — and with it the user's channel edits and the rule's open excursion
        // (deleting a rule cascade-deletes its excursions, which would re-dispatch an
        // already-acknowledged alert on the next sweep):
        //   1. exact baked-minutes match (unchanged thresholds),
        //   2. positional pairing of the remainder by ascending minutes (an edited-hours
        //      threshold reclaims the rule whose old minutes it displaced).
        var pending = definition.NotificationThresholds
            .OrderBy(t => t.DisplayOrder)
            .Select(t => (Threshold: t, Minutes: EffectiveMinutes(definition, t)))
            .ToList();
        var claimedRuleIds = pending
            .Where(p => p.Threshold.AlertRuleId is not null)
            .Select(p => p.Threshold.AlertRuleId!.Value)
            .ToHashSet();
        var adoptable = managedRules.Values
            .Where(r => !claimedRuleIds.Contains(r.Id))
            .Select(r => (Rule: r, Minutes: RuleMinutes(r)))
            .Where(r => r.Minutes is not null)
            .OrderBy(r => r.Minutes)
            .ToList();

        foreach (var (threshold, minutes) in pending)
        {
            if (threshold.AlertRuleId is not null || minutes is null) continue;
            var index = adoptable.FindIndex(a => a.Minutes == minutes);
            if (index < 0) continue;
            threshold.AlertRuleId = adoptable[index].Rule.Id;
            adoptable.RemoveAt(index);
        }
        foreach (var (threshold, minutes) in pending.OrderBy(p => p.Minutes))
        {
            if (threshold.AlertRuleId is not null || minutes is null) continue;
            if (adoptable.Count == 0) break;
            threshold.AlertRuleId = adoptable[0].Rule.Id;
            adoptable.RemoveAt(0);
        }

        var keptRuleIds = new HashSet<Guid>();
        foreach (var (threshold, minutes) in pending)
        {
            if (minutes is null)
            {
                // Misconfigured (negative threshold without lifespan) — matches the legacy
                // evaluator's skip. Leave any previously synced rule in place untouched.
                if (threshold.AlertRuleId is { } existingId) keptRuleIds.Add(existingId);
                continue;
            }

            var conditionParams = JsonSerializer.Serialize(new
            {
                tracker_definition_id = definitionId,
                @operator = ">=",
                minutes = minutes.Value,
            });

            var name = Truncate($"{definition.Name}: {ThresholdLabel(definition, threshold)}", 128);
            var severity = MapSeverity(threshold.Urgency);
            var scopeClass = _scopeClassifier.Classify(AlertConditionType.TrackerAge, conditionParams);

            if (threshold.AlertRuleId is { } ruleId && managedRules.TryGetValue(ruleId, out var rule))
            {
                rule.Name = name;
                rule.Description = threshold.Description;
                rule.ConditionType = AlertConditionType.TrackerAge;
                rule.ConditionParams = conditionParams;
                rule.ScopeClass = scopeClass;
                rule.Severity = severity;
                rule.UpdatedAt = DateTime.UtcNow;
                keptRuleIds.Add(rule.Id);
            }
            else
            {
                var created = new AlertRuleEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    Name = name,
                    Description = threshold.Description,
                    ConditionType = AlertConditionType.TrackerAge,
                    ConditionParams = conditionParams,
                    ScopeClass = scopeClass,
                    Severity = severity,
                    ManagedBy = tag,
                    IsEnabled = true,
                    // Quiet-hours opt-out on the threshold maps onto the alert engine's DND
                    // bypass. Create-time seed only — the rule editor owns it afterwards.
                    AllowThroughDnd = !threshold.RespectQuietHours,
                    ClientConfiguration = BuildClientConfiguration(threshold),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };

                created.Channels.Add(new AlertRuleChannelEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    AlertRuleId = created.Id,
                    ChannelType = ChannelType.InApp,
                    SortOrder = 0,
                    CreatedAt = DateTime.UtcNow,
                });
                if (threshold.PushEnabled)
                {
                    created.Channels.Add(new AlertRuleChannelEntity
                    {
                        Id = Guid.CreateVersion7(),
                        TenantId = tenantId,
                        AlertRuleId = created.Id,
                        ChannelType = ChannelType.WebPush,
                        SortOrder = 1,
                        CreatedAt = DateTime.UtcNow,
                    });
                }

                db.AlertRules.Add(created);
                threshold.AlertRuleId = created.Id;
                keptRuleIds.Add(created.Id);
            }
        }

        var orphaned = managedRules.Values.Where(r => !keptRuleIds.Contains(r.Id)).ToList();
        var removed = await RemoveOrDisableAsync(db, orphaned, ct);

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Synced {ThresholdCount} threshold(s) to managed alert rules for tracker definition {DefinitionId} ({Orphaned} removed)",
            definition.NotificationThresholds.Count, definitionId, removed);
    }

    /// <inheritdoc />
    public async Task DeleteRulesForDefinitionAsync(Guid definitionId, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateAsync(ct);
        var tag = ManagedByTag(definitionId);

        var rules = await db.AlertRules
            .Where(r => r.ManagedBy == tag)
            .ToListAsync(ct);
        if (rules.Count == 0) return;

        var removed = await RemoveOrDisableAsync(db, rules, ct);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Deleted {Count} managed alert rule(s) for removed tracker definition {DefinitionId}",
            removed, definitionId);
    }

    /// <summary>
    /// Deletes the given managed rules — except rules that another rule's
    /// <c>alert_state</c> condition references. Deleting those would silently drop the
    /// referencing (escalation) rule from every evaluation pass, so they are kept but
    /// disabled: the referencing rule visibly stops (disabled-parent semantics, the same
    /// state a user creates by toggling the parent off) instead of dying invisibly, and
    /// the stale condition can no longer fire.
    /// </summary>
    private async Task<int> RemoveOrDisableAsync(
        NocturneDbContext db, IReadOnlyList<AlertRuleEntity> rules, CancellationToken ct)
    {
        var removed = 0;
        foreach (var rule in rules)
        {
            var referencing = await _referenceService.FindReferencingRulesAsync(rule.Id, ct);
            if (referencing.Count > 0)
            {
                rule.IsEnabled = false;
                rule.UpdatedAt = DateTime.UtcNow;
                _logger.LogWarning(
                    "Managed rule {RuleId} ('{RuleName}') is referenced by alert_state rule(s) {ReferencingIds}; disabled instead of deleted",
                    rule.Id, rule.Name, string.Join(", ", referencing));
                continue;
            }

            db.AlertRules.Remove(rule);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// The baked minutes of a managed rule's stored condition, or null when the stored
    /// params can't be read (never written by this service, but fail soft).
    /// </summary>
    private static int? RuleMinutes(AlertRuleEntity rule)
    {
        try
        {
            using var doc = JsonDocument.Parse(rule.ConditionParams);
            return doc.RootElement.TryGetProperty("minutes", out var minutes)
                && minutes.ValueKind == JsonValueKind.Number
                && minutes.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a threshold's <c>hours</c> into the tracker_age condition's minutes,
    /// mirroring the legacy evaluator: Event thresholds are relative to the scheduled
    /// time (negative = before), Duration thresholds relative to start, and a negative
    /// Duration threshold means "N hours before the lifespan ends" and is baked in as
    /// <c>lifespan + hours</c>. Null when a negative Duration threshold has no lifespan.
    /// </summary>
    private static int? EffectiveMinutes(
        TrackerDefinitionEntity definition, TrackerNotificationThresholdEntity threshold)
    {
        if (definition.Mode == TrackerMode.Event || threshold.Hours >= 0)
            return threshold.Hours * 60;

        if (definition.LifespanHours is not { } lifespan)
            return null;

        return (lifespan + threshold.Hours) * 60;
    }

    private static string ThresholdLabel(
        TrackerDefinitionEntity definition, TrackerNotificationThresholdEntity threshold)
    {
        if (!string.IsNullOrWhiteSpace(threshold.Description))
            return threshold.Description;

        if (definition.Mode == TrackerMode.Event)
        {
            return threshold.Hours < 0
                ? $"{Math.Abs(threshold.Hours)}h before"
                : $"{threshold.Hours}h after";
        }

        return threshold.Hours < 0
            ? $"{Math.Abs(threshold.Hours)}h before end"
            : $"{threshold.Hours}h elapsed";
    }

    private static AlertRuleSeverity MapSeverity(NotificationUrgency urgency) => urgency switch
    {
        NotificationUrgency.Urgent => AlertRuleSeverity.Critical,
        NotificationUrgency.Hazard => AlertRuleSeverity.Warning,
        NotificationUrgency.Warn => AlertRuleSeverity.Warning,
        _ => AlertRuleSeverity.Info,
    };

    /// <summary>
    /// Seeds the rule's client presentation config from the threshold's legacy audio
    /// settings. Create-time seed only — the rule editor owns it afterwards.
    /// </summary>
    private static string BuildClientConfiguration(TrackerNotificationThresholdEntity threshold)
    {
        if (!threshold.AudioEnabled && !threshold.VibrateEnabled)
            return "{}";

        return JsonSerializer.Serialize(new
        {
            audioEnabled = threshold.AudioEnabled,
            audioSound = threshold.AudioSound,
            vibrateEnabled = threshold.VibrateEnabled,
        });
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
