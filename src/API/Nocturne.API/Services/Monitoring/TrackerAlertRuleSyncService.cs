using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
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
    private readonly TimeProvider _time;
    private readonly ILogger<TrackerAlertRuleSyncService> _logger;

    /// <summary>Initialises a new <see cref="TrackerAlertRuleSyncService"/>.</summary>
    public TrackerAlertRuleSyncService(
        INocturneDbContextFactory contextFactory,
        IRuleScopeClassifier scopeClassifier,
        IAlertReferenceService referenceService,
        AlertRuleRearm rearm,
        AlertRuleRetirement retirement,
        TimeProvider timeProvider,
        ILogger<TrackerAlertRuleSyncService> logger)
    {
        _contextFactory = contextFactory;
        _scopeClassifier = scopeClassifier;
        _referenceService = referenceService;
        _rearm = rearm;
        _retirement = retirement;
        _time = timeProvider;
        _logger = logger;
    }

    /// <summary>The <see cref="AlertRuleRecord.ManagedBy"/> tag for a tracker definition's rules.</summary>
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
        if ((await new AlertRuleRepository(unit, _time).GetManagedRulesAsync(tag, ct)).Count > 0)
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
        var rules = new AlertRuleRepository(unit, _time);
        var managedRules = await rules.GetManagedRulesAsync(tag, ct);
        if (managedRules.Count == 0
            && definition.NotificationThresholds.Count == 0
            && !(definition.LowReservoirUnits is not null && TrackerCoverage.HoldsInsulin(definition)))
            return;

        var recipient = await InAppRecipientOfAsync(new TenantMembershipRepository(unit), tenantId, definition, ct);

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
            .Where(r => !claimedRuleIds.Contains(r.Rule.Id))
            .Select(r => (Rule: r, Minutes: RuleMinutes(r)))
            .Where(r => r.Minutes is not null)
            .OrderBy(r => r.Minutes)
            .ToList();

        foreach (var (threshold, minutes) in pending)
        {
            if (threshold.AlertRuleId is not null || minutes is null) continue;
            var index = adoptable.FindIndex(a => a.Minutes == minutes);
            if (index < 0) continue;
            threshold.AlertRuleId = adoptable[index].Rule.Rule.Id;
            adoptable.RemoveAt(index);
        }
        foreach (var (threshold, minutes) in pending.OrderBy(p => p.Minutes))
        {
            if (threshold.AlertRuleId is not null || minutes is null) continue;
            if (adoptable.Count == 0) break;
            threshold.AlertRuleId = adoptable[0].Rule.Rule.Id;
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
                var same = rule.Rule.ConditionType == AlertConditionType.TrackerAge
                    && ConditionTreeEquality.Same(rule.Rule.ConditionParams, conditionParams);
                if (!same)
                    changed.Add(ruleId);
                await rules.UpdateAsync(ruleId, AlertRuleWrite.Of(rule) with
                {
                    Name = name,
                    Description = threshold.Description,
                    ConditionType = AlertConditionType.TrackerAge,
                    ConditionParams = same ? rule.Rule.ConditionParams : conditionParams,
                    ScopeClass = scopeClass,
                    Severity = severity,
                }, ct);
                keptRuleIds.Add(ruleId);
            }
            else
            {
                var createdId = await rules.CreateAsync(
                    ManagedRule(name, threshold.Description, AlertConditionType.TrackerAge, conditionParams, scopeClass, severity, tag, recipient),
                    ct);
                threshold.AlertRuleId = createdId;
                keptRuleIds.Add(createdId);
            }
        }

        await SyncReservoirLevelRuleAsync(rules, definition, recipient, tag, managedRules, keptRuleIds, changed, ct);

        var orphaned = managedRules.Values.Where(r => !keptRuleIds.Contains(r.Rule.Id)).ToList();
        var deleted = new List<Guid>();
        var removed = await RemoveOrDisableAsync(rules, orphaned, disabled, deleted, ct);

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

        var rules = new AlertRuleRepository(unit, _time);
        var managed = (await rules.GetManagedRulesAsync(tag, ct)).Values.ToList();
        if (managed.Count == 0) return;

        var disabled = new List<Guid>();
        var deleted = new List<Guid>();
        var removed = await RemoveOrDisableAsync(rules, managed, disabled, deleted, ct);
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
        IAlertRuleRepository rules, IReadOnlyList<AlertRuleRecord> managed,
        List<Guid> disabled, List<Guid> deleted, CancellationToken ct)
    {
        var removed = 0;
        foreach (var rule in managed)
        {
            var ruleId = rule.Rule.Id;
            var referencing = await _referenceService.FindReferencingRulesAsync(ruleId, ct);
            if (referencing.Count > 0)
            {
                if (rule.IsEnabled)
                    disabled.Add(ruleId);
                await rules.UpdateAsync(ruleId, AlertRuleWrite.Of(rule) with { IsEnabled = false }, ct);
                _logger.LogWarning(
                    "Managed rule {RuleId} ('{RuleName}') is referenced by alert_state rule(s) {ReferencingIds}; disabled instead of deleted",
                    ruleId, rule.Rule.Name, string.Join(", ", referencing));
                continue;
            }

            if (rule.IsEnabled)
                deleted.Add(ruleId);
            await rules.DeleteAsync(ruleId, ct);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Synthesises the single reservoir-level rule for a definition that holds insulin
    /// (<see cref="TrackerCoverage.HoldsInsulin(TrackerDefinitionEntity)"/>) with
    /// <see cref="TrackerDefinitionEntity.LowReservoirUnits"/> set. The rule is
    /// identified within the definition's managed set by its Reservoir condition type
    /// (there is at most one), so edits adopt the existing rule and preserve user edits
    /// exactly like the tracker_age rules. When the units are cleared — or the definition
    /// stops holding insulin — the rule is not marked kept, so the shared orphan
    /// pass removes it.
    /// </summary>
    private async Task SyncReservoirLevelRuleAsync(
        IAlertRuleRepository rules,
        TrackerDefinitionEntity definition,
        string recipient,
        string tag,
        IReadOnlyDictionary<Guid, AlertRuleRecord> managedRules,
        HashSet<Guid> keptRuleIds,
        List<Guid> changed,
        CancellationToken ct)
    {
        if (!TrackerCoverage.HoldsInsulin(definition) || definition.LowReservoirUnits is not { } units)
            return;

        // Snake_case, the condition models' wire form (ReservoirCondition).
        var conditionParams = JsonSerializer.Serialize(new { @operator = "<", value = units });
        var name = Truncate($"{definition.Name}: below {units} U", 128);
        var severity = MapSeverity(definition.LowReservoirUrgency);
        var scopeClass = _scopeClassifier.Classify(AlertConditionType.Reservoir, conditionParams);

        var existing = managedRules.Values
            .FirstOrDefault(r => r.Rule.ConditionType == AlertConditionType.Reservoir);
        if (existing is not null)
        {
            var same = ConditionTreeEquality.Same(existing.Rule.ConditionParams, conditionParams);
            if (!same)
                changed.Add(existing.Rule.Id);
            await rules.UpdateAsync(existing.Rule.Id, AlertRuleWrite.Of(existing) with
            {
                Name = name,
                ConditionParams = same ? existing.Rule.ConditionParams : conditionParams,
                ScopeClass = scopeClass,
                Severity = severity,
            }, ct);
            keptRuleIds.Add(existing.Rule.Id);
            return;
        }

        keptRuleIds.Add(await rules.CreateAsync(
            ManagedRule(name, null, AlertConditionType.Reservoir, conditionParams, scopeClass, severity, tag, recipient),
            ct));
    }

    /// <summary>
    /// A new managed rule, enabled, with one <c>in_app</c> channel to <paramref name="recipient"/>
    /// and the user-editable fields at their defaults.
    /// </summary>
    private static NewAlertRule ManagedRule(
        string name,
        string? description,
        AlertConditionType conditionType,
        string conditionParams,
        RuleScopeClass scopeClass,
        AlertRuleSeverity severity,
        string tag,
        string recipient) => new(
        new AlertRuleWrite(
            name, description, conditionType, conditionParams, scopeClass, severity,
            IsEnabled: true, SortOrder: 0, AllowThroughDnd: false,
            AutoResolveEnabled: false, AutoResolveParams: null, ClientConfiguration: "{}"),
        [new AlertRuleChannelWrite(ChannelType.InApp, recipient)],
        ManagedBy: tag);

    /// <summary>
    /// Who a synced rule's in-app channel is sent to: the member who created the tracker definition,
    /// or the tenant's longest-standing owner when that is not a person who may receive, as for a
    /// definition created through a site token, which belongs to the device account. Empty when
    /// the tenant has neither, which delivery records as failed.
    /// </summary>
    private static async Task<string> InAppRecipientOfAsync(
        ITenantMembershipRepository members, Guid tenantId, TrackerDefinitionEntity definition, CancellationToken ct)
    {
        var recipient = await InAppRecipients.ResolveAsync(members, tenantId, definition.CreatedBy, ct)
            ?? await InAppRecipients.EarliestOwnerAsync(members, tenantId, ct);
        return recipient?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// The baked minutes of a managed rule's stored condition, or null when the stored
    /// params can't be read (never written by this service, but fail soft).
    /// </summary>
    private static int? RuleMinutes(AlertRuleRecord rule)
    {
        try
        {
            using var doc = JsonDocument.Parse(rule.Rule.ConditionParams);
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
