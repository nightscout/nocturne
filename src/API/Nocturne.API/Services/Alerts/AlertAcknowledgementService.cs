using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Acknowledges active alert excursions either in bulk for a whole tenant
/// (<see cref="AcknowledgeAllAsync"/>) or one excursion at a time
/// (<see cref="AcknowledgeExcursionAsync"/>). Acknowledgement halts escalation
/// but does not close the excursion — hysteresis still runs.
/// </summary>
/// <seealso cref="IAlertAcknowledgementService"/>
/// <seealso cref="ISignalRBroadcastService"/>
internal sealed class AlertAcknowledgementService(
    IDbContextFactory<NocturneDbContext> contextFactory,
    ITenantAccessor tenantAccessor,
    ISignalRBroadcastService broadcastService,
    ILogger<AlertAcknowledgementService> logger)
    : IAlertAcknowledgementService
{
    /// <summary>
    /// Creates a fresh pooled <see cref="NocturneDbContext"/> with <see cref="NocturneDbContext.TenantId"/>
    /// set from the supplied tenant or the ambient <see cref="ITenantAccessor"/>. Necessary because
    /// <see cref="IDbContextFactory{TContext}.CreateDbContextAsync"/> hands back a raw context
    /// (TenantId == Guid.Empty) which the global tenant query filter then uses to exclude every row.
    /// </summary>
    private async Task<NocturneDbContext> CreateContextForAsync(Guid tenantId, CancellationToken ct)
    {
        var db = await contextFactory.CreateDbContextAsync(ct);
        db.TenantId = tenantId == Guid.Empty
            ? (tenantAccessor.IsResolved ? tenantAccessor.TenantId : Guid.Empty)
            : tenantId;
        return db;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Loads all open excursions (where <c>EndedAt IS NULL</c>), applies the
    /// per-excursion mutation via <see cref="ApplyAcknowledgement"/>, saves once,
    /// and broadcasts a single roll-up <c>alert_acknowledged</c> event via
    /// <see cref="ISignalRBroadcastService"/>. Broadcast failures are swallowed
    /// and logged so the acknowledgement is not rolled back.
    /// </remarks>
    public async Task AcknowledgeAllAsync(Guid tenantId, string acknowledgedBy, CancellationToken ct)
    {
        await using var db = await CreateContextForAsync(tenantId, ct);
        var now = DateTime.UtcNow;

        var excursions = await db.AlertExcursions
            .Where(e => e.TenantId == tenantId && e.EndedAt == null)
            .ToListAsync(ct);

        if (excursions.Count == 0)
        {
            logger.LogDebug("No active excursions to acknowledge for tenant {TenantId}", tenantId);
            return;
        }

        var excursionIds = excursions.Select(e => e.Id).ToList();
        var instances = await db.AlertInstances
            .Where(i => excursionIds.Contains(i.AlertExcursionId) && i.ResolvedAt == null)
            .ToListAsync(ct);

        var instancesByExcursion = instances
            .GroupBy(i => i.AlertExcursionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var excursion in excursions)
        {
            instancesByExcursion.TryGetValue(excursion.Id, out var excursionInstances);
            ApplyAcknowledgement(excursion, excursionInstances, acknowledgedBy, now);
        }

        await db.SaveChangesAsync(ct);

        try
        {
            await broadcastService.BroadcastAlertEventAsync("alert_acknowledged", new
            {
                tenantId,
                acknowledgedBy,
                acknowledgedAt = now,
                excursionCount = excursions.Count,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast alert_acknowledged for tenant {TenantId}", tenantId);
        }

        logger.LogInformation(
            "Acknowledged {ExcursionCount} excursions and {InstanceCount} instances for tenant {TenantId} by {AcknowledgedBy}",
            excursions.Count, instances.Count, tenantId, acknowledgedBy);
    }

    /// <inheritdoc/>
    public async Task AcknowledgeExcursionAsync(
        Guid tenantId, Guid excursionId, string acknowledgedBy, bool broadcast, CancellationToken ct)
    {
        await using var db = await CreateContextForAsync(tenantId, ct);
        var now = DateTime.UtcNow;

        // Tenant filter is implicit via db.TenantId; the explicit TenantId == tenantId clause is
        // defence-in-depth so a passed tenantId of Guid.Empty (never matches a real row) cannot
        // leak across tenants.
        var excursion = await db.AlertExcursions
            .FirstOrDefaultAsync(e => e.Id == excursionId
                                       && e.TenantId == tenantId
                                       && e.EndedAt == null, ct);

        if (excursion is null)
        {
            logger.LogDebug("Excursion {ExcursionId} not found or already closed; nothing to acknowledge", excursionId);
            return;
        }

        if (excursion.AcknowledgedAt is not null)
        {
            return;
        }

        var instances = await db.AlertInstances
            .Where(i => i.AlertExcursionId == excursionId && i.ResolvedAt == null)
            .ToListAsync(ct);

        ApplyAcknowledgement(excursion, instances, acknowledgedBy, now);

        await db.SaveChangesAsync(ct);

        if (broadcast)
        {
            try
            {
                await broadcastService.BroadcastAlertEventAsync("alert_acknowledged", new
                {
                    tenantId = excursion.TenantId,
                    excursionId,
                    acknowledgedBy,
                    acknowledgedAt = now,
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to broadcast alert_acknowledged for excursion {ExcursionId}", excursionId);
            }
        }

        logger.LogInformation(
            "Acknowledged excursion {ExcursionId} ({InstanceCount} instances) by {AcknowledgedBy}",
            excursionId, instances.Count, acknowledgedBy);
    }

    private static void ApplyAcknowledgement(
        AlertExcursionEntity excursion,
        List<AlertInstanceEntity>? instances,
        string acknowledgedBy,
        DateTime now)
    {
        excursion.AcknowledgedAt = now;
        excursion.AcknowledgedBy = acknowledgedBy;

        if (instances is null) return;

        foreach (var instance in instances)
        {
            instance.Status = "acknowledged";
        }
    }
}
