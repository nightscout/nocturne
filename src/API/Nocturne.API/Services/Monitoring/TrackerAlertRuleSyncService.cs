using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Conditions;
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
    /// Creates the managed alert rules of <paramref name="definitionId"/>, a definition that has
    /// none yet, on the caller's <paramref name="unit"/> and inside its transaction, so the rules
    /// commit with the definition. Having no rules, it retires none, so nothing is closed
    /// before the caller commits.
    /// </summary>
    /// <exception cref="InvalidOperationException">The definition already has managed rules.</exception>
    Task CreateRulesAsync(DbUnitOfWork unit, Guid definitionId, CancellationToken ct = default);

    /// <summary>
    /// Deletes every managed alert rule owned by <paramref name="definitionId"/>. Called
    /// when the tracker definition itself is deleted.
    /// </summary>
    Task DeleteRulesForDefinitionAsync(Guid definitionId, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class TrackerAlertRuleSyncService : ITrackerAlertRuleSyncService
{
    private readonly INocturneDbContextFactory _contextFactory;
    private readonly IRuleScopeClassifier _scopeClassifier;
    private readonly IAlertReferenceService _referenceService;
    private readonly AlertRuleRearm _rearm;
    private readonly AlertRuleRetirement _retirement;
    private readonly ILogger<TrackerAlertRuleSyncService> _logger;

    /// <summary>Initialises a new <see cref="TrackerAlertRuleSyncService"/>.</summary>
    public TrackerAlertRuleSyncService(
        INocturneDbContextFactory contextFactory,
        IRuleScopeClassifier scopeClassifier,
        IAlertReferenceService referenceService,
        AlertRuleRearm rearm,
        AlertRuleRetirement retirement,
        ILogger<TrackerAlertRuleSyncService> logger)
    {
        _contextFactory = contextFactory;
        _scopeClassifier = scopeClassifier;
        _referenceService = referenceService;
        _rearm = rearm;
        _retirement = retirement;
        _logger = logger;
    }

    /// <summary>The <see cref="AlertRuleEntity.ManagedBy"/> tag for a tracker definition's rules.</summary>
    public static string ManagedByTag(Guid definitionId) => $"tracker:{definitionId}";

    /// <inheritdoc />
    public async Task SyncDefinitionAsync(Guid definitionId, CancellationToken ct = default)
    {
        await using var unit = await DbUnitOfWork.OpenAsync(_contextFactory, ct);
        await SyncAsync(unit, definitionId, ct);
    }

    /// <inheritdoc />
    public async Task CreateRulesAsync(DbUnitOfWork unit, Guid definitionId, CancellationToken ct = default)
    {
        var tag = ManagedByTag(definitionId);
        if (await unit.Context.AlertRules.AnyAsync(r => r.ManagedBy == tag, ct))
            throw new InvalidOperationException($"Tracker definition {definitionId} already has managed rules");

        await SyncAsync(unit, definitionId, ct);
    }

    private async Task SyncAsync(DbUnitOfWork unit, Guid definitionId, CancellationToken ct)
    {
        var db = unit.Context;
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
        if (managedRules.Count == 0
            && definition.NotificationThresholds.Count == 0
            && definition is not { Category: TrackerCategory.Reservoir, LowReservoirUnits: not null })
            return;

        var recipient = await InAppRecipientOfAsync(db, tenantId, definition, ct);

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
            .Select(t => (Threshold: t, Minutes: TrackerSchedule.OffsetMinutes(definition.Mode, definition.LifespanHours, t.Hours)))
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
        var changed = new List<Guid>();
        var disabled = new List<Guid>();
        foreach (var (threshold, minutes) in pending)
        {
            if (minutes is null)
            {
                // A negative threshold without a lifespan can never fire. Leave any previously
                // synced rule in place untouched.
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
                if (rule.ConditionType != AlertConditionType.TrackerAge
                    || !ConditionTreeEquality.Same(rule.ConditionParams, conditionParams))
                {
                    changed.Add(rule.Id);
                    rule.ConditionParams = conditionParams;
                }
                rule.Name = name;
                rule.Description = threshold.Description;
                rule.ConditionType = AlertConditionType.TrackerAge;
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
                    ClientConfiguration = "{}",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };

                created.Channels.Add(InAppChannel(tenantId, created.Id, recipient));

                db.AlertRules.Add(created);
                threshold.AlertRuleId = created.Id;
                keptRuleIds.Add(created.Id);
            }
        }

        SyncReservoirLevelRule(db, tenantId, definition, recipient, tag, managedRules, keptRuleIds, changed);

        var orphaned = managedRules.Values.Where(r => !keptRuleIds.Contains(r.Id)).ToList();
        var deleted = new List<Guid>();
        var removed = await RemoveOrDisableAsync(db, orphaned, disabled, deleted, ct);

        // AlertRuleRetirement's remarks: a delete closes before the save that removes the rule.
        if (deleted.Count > 0)
            await _retirement.CloseAsync(deleted, tenantId, ct);

        await _rearm.SaveAndClearAsync(unit, [.. changed, .. disabled], ct);
        if (disabled.Count > 0)
            await _retirement.CloseAsync(disabled, tenantId, CancellationToken.None);

        _logger.LogInformation(
            "Synced {ThresholdCount} threshold(s) to managed alert rules for tracker definition {DefinitionId} ({Orphaned} removed)",
            definition.NotificationThresholds.Count, definitionId, removed);
    }

    /// <inheritdoc />
    public async Task DeleteRulesForDefinitionAsync(Guid definitionId, CancellationToken ct = default)
    {
        await using var unit = await DbUnitOfWork.OpenAsync(_contextFactory, ct);
        var db = unit.Context;
        var tag = ManagedByTag(definitionId);

        var rules = await db.AlertRules
            .Where(r => r.ManagedBy == tag)
            .ToListAsync(ct);
        if (rules.Count == 0) return;

        var disabled = new List<Guid>();
        var deleted = new List<Guid>();
        var removed = await RemoveOrDisableAsync(db, rules, disabled, deleted, ct);
        // AlertRuleRetirement's remarks: a delete closes before the save that removes the rule.
        if (deleted.Count > 0)
            await _retirement.CloseAsync(deleted, db.TenantId, ct);
        await _rearm.SaveAndClearAsync(unit, disabled, ct);
        if (disabled.Count > 0)
            await _retirement.CloseAsync(disabled, db.TenantId, CancellationToken.None);

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
    /// the stale condition can no longer fire. Each disabled rule is added to
    /// <paramref name="disabled"/>; each enabled rule removed is added to
    /// <paramref name="deleted"/> so the caller can close its excursion before the save.
    /// </summary>
    private async Task<int> RemoveOrDisableAsync(
        NocturneDbContext db, IReadOnlyList<AlertRuleEntity> rules,
        List<Guid> disabled, List<Guid> deleted, CancellationToken ct)
    {
        var removed = 0;
        foreach (var rule in rules)
        {
            var referencing = await _referenceService.FindReferencingRulesAsync(rule.Id, ct);
            if (referencing.Count > 0)
            {
                if (rule.IsEnabled)
                    disabled.Add(rule.Id);
                rule.IsEnabled = false;
                rule.UpdatedAt = DateTime.UtcNow;
                _logger.LogWarning(
                    "Managed rule {RuleId} ('{RuleName}') is referenced by alert_state rule(s) {ReferencingIds}; disabled instead of deleted",
                    rule.Id, rule.Name, string.Join(", ", referencing));
                continue;
            }

            if (rule.IsEnabled)
                deleted.Add(rule.Id);
            db.AlertRules.Remove(rule);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Synthesises the single reservoir-level rule for a Reservoir-category definition
    /// with <see cref="TrackerDefinitionEntity.LowReservoirUnits"/> set. The rule is
    /// identified within the definition's managed set by its Reservoir condition type
    /// (there is at most one), so edits adopt the existing rule and preserve user edits
    /// exactly like the tracker_age rules. When the units are cleared — or the category
    /// changes away from Reservoir — the rule is not marked kept, so the shared orphan
    /// pass removes it.
    /// </summary>
    private void SyncReservoirLevelRule(
        NocturneDbContext db,
        Guid tenantId,
        TrackerDefinitionEntity definition,
        string recipient,
        string tag,
        IReadOnlyDictionary<Guid, AlertRuleEntity> managedRules,
        HashSet<Guid> keptRuleIds,
        List<Guid> changed)
    {
        if (definition.Category != TrackerCategory.Reservoir || definition.LowReservoirUnits is not { } units)
            return;

        // Snake_case, the condition models' wire form (ReservoirCondition).
        var conditionParams = JsonSerializer.Serialize(new { @operator = "<", value = units });
        var name = Truncate($"{definition.Name}: below {units} U", 128);
        var severity = MapSeverity(definition.LowReservoirUrgency);
        var scopeClass = _scopeClassifier.Classify(AlertConditionType.Reservoir, conditionParams);

        var existing = managedRules.Values
            .FirstOrDefault(r => r.ConditionType == AlertConditionType.Reservoir);
        if (existing is not null)
        {
            if (!ConditionTreeEquality.Same(existing.ConditionParams, conditionParams))
            {
                changed.Add(existing.Id);
                existing.ConditionParams = conditionParams;
            }
            existing.Name = name;
            existing.ScopeClass = scopeClass;
            existing.Severity = severity;
            existing.UpdatedAt = DateTime.UtcNow;
            keptRuleIds.Add(existing.Id);
            return;
        }

        var created = new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = name,
            ConditionType = AlertConditionType.Reservoir,
            ConditionParams = conditionParams,
            ScopeClass = scopeClass,
            Severity = severity,
            ManagedBy = tag,
            IsEnabled = true,
            ClientConfiguration = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        created.Channels.Add(InAppChannel(tenantId, created.Id, recipient));
        db.AlertRules.Add(created);
        keptRuleIds.Add(created.Id);
    }

    /// <summary>
    /// Who a synced rule's in-app channel is sent to: the member who created the tracker definition,
    /// or the tenant's longest-standing owner when that is not a person who may receive, as for a
    /// definition created through a site token, which belongs to the device account. Empty when
    /// the tenant has neither, which delivery records as failed.
    /// </summary>
    private static async Task<string> InAppRecipientOfAsync(
        NocturneDbContext db, Guid tenantId, TrackerDefinitionEntity definition, CancellationToken ct)
    {
        var recipient = await InAppRecipients.ResolveAsync(db, tenantId, definition.CreatedBy, ct)
            ?? await InAppRecipients.EarliestOwnerAsync(db, tenantId, ct);
        return recipient?.ToString() ?? string.Empty;
    }

    private static AlertRuleChannelEntity InAppChannel(Guid tenantId, Guid ruleId, string recipient) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        AlertRuleId = ruleId,
        ChannelType = ChannelType.InApp,
        Destination = recipient,
        SortOrder = 0,
        CreatedAt = DateTime.UtcNow,
    };

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

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
