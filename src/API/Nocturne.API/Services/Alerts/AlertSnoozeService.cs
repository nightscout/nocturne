using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Alerts;

/// <inheritdoc cref="IAlertSnoozeService"/>
/// <remarks>
/// Suppression itself is not enforced here: <see cref="AlertDeliveryService.DispatchAsync"/> is
/// the gate every delivery passes, and the continuous surfaces (the device active-intents
/// snapshot, the Home Assistant reconnect replay) read <see cref="AlertSnooze"/> directly.
/// </remarks>
internal sealed class AlertSnoozeService(
    IDbContextFactory<NocturneDbContext> contextFactory,
    ITenantAccessor tenantAccessor,
    IAlertRepository repository,
    IAlertDeliveryService deliveryService,
    ISensorContextEnricher contextEnricher,
    ICanonicalGlucoseService canonicalGlucose,
    ISignalRBroadcastService broadcastService,
    TimeProvider timeProvider,
    ILogger<AlertSnoozeService> logger)
    : IAlertSnoozeService
{
    public async Task<SnoozeOutcome> SnoozeAsync(Guid instanceId, int minutes, CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.TenantId = tenantId;

        var instance = await db.AlertInstances
            .Include(i => i.AlertExcursion)
                .ThenInclude(e => e!.AlertRule)
            .FirstOrDefaultAsync(i => i.Id == instanceId, ct);

        if (instance is null)
            return new SnoozeOutcome(SnoozeResult.NotFound);

        if (instance.ResolvedAt is not null || instance.AlertExcursion?.EndedAt is not null)
            return new SnoozeOutcome(SnoozeResult.NotActive);

        // Manual snoozes and smart-snooze extensions spend the same count; see SmartSnoozeConfig.
        if (instance.SnoozeCount >= SmartSnoozeConfig.Parse(instance.AlertExcursion?.AlertRule?.ClientConfiguration).MaxCount)
            return new SnoozeOutcome(SnoozeResult.LimitReached);

        var until = now.AddMinutes(minutes);
        instance.SnoozedUntil = until;
        instance.SnoozeCount++;
        await db.SaveChangesAsync(ct);

        // A chat-bot delivery still pending would otherwise be picked up by the bot's poll
        // mid-snooze; the resume dispatch creates fresh rows.
        await repository.ExpirePendingDeliveriesAsync(tenantId, [instanceId], ct);

        try
        {
            await broadcastService.BroadcastAlertEventAsync("alert_snoozed", new
            {
                tenantId,
                excursionId = instance.AlertExcursionId,
                instanceId,
                snoozedUntil = until,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast alert_snoozed for instance {InstanceId}", instanceId);
        }

        logger.LogInformation(
            "Alert instance {InstanceId} snoozed until {SnoozedUntil} (count {Count})",
            instanceId, until, instance.SnoozeCount);

        return new SnoozeOutcome(SnoozeResult.Snoozed, until);
    }

    public async Task<bool> ResumeAsync(Guid instanceId, CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        AlertInstanceEntity? instance;
        await using (var db = await contextFactory.CreateDbContextAsync(ct))
        {
            db.TenantId = tenantId;

            instance = await db.AlertInstances
                .AsNoTracking()
                .Include(i => i.AlertExcursion)
                    .ThenInclude(e => e!.AlertRule)
                .FirstOrDefaultAsync(i => i.Id == instanceId, ct);
        }

        var excursion = instance?.AlertExcursion;
        var ruleEntity = excursion?.AlertRule;
        if (instance is null || excursion is null || ruleEntity is null) return false;

        if (instance.ResolvedAt is not null
            || excursion.EndedAt is not null
            || excursion.HysteresisStartedAt is not null
            || excursion.AcknowledgedAt is not null
            || !ruleEntity.IsEnabled
            || AlertSnooze.IsSnoozed(instance.SnoozedUntil, now))
        {
            return false;
        }

        // Same freshness bar as the sweep's snooze context: a stale value would describe a patient
        // who may have moved on, so the re-notification carries no reading instead.
        var latest = await canonicalGlucose.GetLatestAsync(ct);
        var fresh = latest is not null && now - latest.Timestamp <= SmartSnoozeTrendGate.MaxLatestAge
            ? latest
            : null;
        var baseContext = new SensorContext
        {
            LatestValue = fresh is null ? null : (decimal)fresh.Mgdl,
            LatestTimestamp = fresh?.Timestamp,
            TrendRate = (decimal?)fresh?.TrendRate,
            LastReadingAt = latest?.Timestamp,
        };

        var rule = new AlertRuleSnapshot(
            ruleEntity.Id, tenantId, ruleEntity.Name, ruleEntity.ConditionType,
            ruleEntity.ConditionParams, ruleEntity.Severity, ruleEntity.ClientConfiguration,
            ruleEntity.SortOrder, ruleEntity.AutoResolveEnabled, ruleEntity.AutoResolveParams,
            ruleEntity.AllowThroughDnd, ruleEntity.ScopeClass);

        // The enricher is only consulted for the active DND scopes. If it fails the alert is
        // delivered: missing a still-standing alert is the worse error.
        var context = baseContext;
        try
        {
            context = await contextEnricher.EnrichAsync(baseContext, [rule], tenantId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to enrich context for snooze resume of instance {InstanceId}; delivering without DND check",
                instanceId);
        }

        if (DndSuppressionGate.SuppressingScope(rule, context.ActiveDndScopes) is { } scope)
        {
            logger.LogInformation(
                "Snooze on alert instance {InstanceId} lapsed while DND (scope {Scope}) covers rule {RuleName}; not re-notifying",
                instanceId, scope, rule.Name);
            return false;
        }

        var channels = await repository.GetChannelsForRuleAsync(tenantId, rule.Id, ct);
        var activeExcursionCount = await repository.CountActiveExcursionsAsync(tenantId, ct);
        var tenant = await repository.GetTenantAlertContextAsync(tenantId, ct);

        var payload = AlertPayloads.Build(
            rule, baseContext, tenantId, excursion.Id, instance.Id, tenant, activeExcursionCount, now);

        await deliveryService.DispatchAsync(instance.Id, channels, payload, ct);

        logger.LogInformation(
            "Snooze on alert instance {InstanceId} lapsed with rule {RuleName} still firing; re-notified",
            instanceId, rule.Name);
        return true;
    }
}
