using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Acknowledges active alert excursions either in bulk for a whole tenant
/// (<see cref="AcknowledgeAllAsync"/>) or one excursion at a time
/// (<see cref="AcknowledgeExcursionAsync"/>), which decides between acknowledging for everyone
/// and muting for the caller. Acknowledgement halts escalation but does not close the
/// excursion; hysteresis still runs.
/// </summary>
/// <seealso cref="IAlertAcknowledgementService"/>
/// <seealso cref="ISignalRBroadcastService"/>
internal sealed class AlertAcknowledgementService(
    IDbContextFactory<NocturneDbContext> contextFactory,
    ITenantAccessor tenantAccessor,
    ISignalRBroadcastService broadcastService,
    ITenantMemberService memberService,
    ILogger<AlertAcknowledgementService> logger,
    IAuditContext? auditContext = null)
    : IAlertAcknowledgementService
{
    /// <summary>
    /// Creates a fresh pooled <see cref="NocturneDbContext"/> with <see cref="NocturneDbContext.TenantId"/>
    /// set from the supplied tenant or the ambient <see cref="ITenantAccessor"/>. Necessary because
    /// <see cref="IDbContextFactory{TContext}.CreateDbContextAsync"/> hands back a raw context
    /// (TenantId == Guid.Empty) which the global tenant query filter then uses to exclude every row.
    /// The scope's audit context is carried too, as <c>ITenantDbContextFactory</c> does: the pooled
    /// context is leased with the property cleared, and an auto-acknowledgement made during a
    /// connector sync would otherwise fall back to the enclosing HTTP request's actor.
    /// </summary>
    private async Task<NocturneDbContext> CreateContextForAsync(Guid tenantId, CancellationToken ct)
    {
        var db = await contextFactory.CreateDbContextAsync(ct);
        db.TenantId = tenantId == Guid.Empty
            ? (tenantAccessor.IsResolved ? tenantAccessor.TenantId : Guid.Empty)
            : tenantId;
        db.AuditContext = auditContext;
        return db;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Loads all still-active excursions (<c>EndedAt</c> null or in the future — device_action
    /// test fires carry a short future <c>EndedAt</c> so they surface in the active-intents
    /// snapshot; see <see cref="AlertDeliveryService.TestFireAsync"/>), applies the
    /// per-excursion mutation via <see cref="ApplyAcknowledgement"/>, saves once,
    /// and broadcasts a single roll-up <c>alert_acknowledged</c> event via
    /// <see cref="ISignalRBroadcastService"/>. Broadcast failures are swallowed
    /// and logged so the acknowledgement is not rolled back.
    /// </remarks>
    public async Task AcknowledgeAllAsync(Guid tenantId, string acknowledgedBy, CancellationToken ct)
    {
        await using var db = await CreateContextForAsync(tenantId, ct);
        var now = DateTime.UtcNow;

        // EndedAt > now matches the active-intents snapshot filter: a device_action test fire
        // keeps the tray flashing for its window, so it must be acknowledgeable during it.
        var excursions = await db.AlertExcursions
            .Where(e => e.TenantId == tenantId && (e.EndedAt == null || e.EndedAt > now))
            .ToListAsync(ct);

        if (excursions.Count == 0)
        {
            logger.LogDebug("No active excursions to acknowledge for tenant {TenantId}", tenantId);
            return;
        }

        var excursionIds = excursions.Select(e => e.Id).ToHashSet();
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
    public async Task<AlertAcknowledgementOutcome> AcknowledgeExcursionAsync(
        Guid tenantId,
        Guid excursionId,
        string acknowledgedBy,
        AlertAcknowledgementAuthority caller,
        bool broadcast,
        CancellationToken ct)
    {
        await using var db = await CreateContextForAsync(tenantId, ct);
        var now = DateTime.UtcNow;

        // Tenant filter is implicit via db.TenantId; the explicit TenantId == tenantId clause is
        // defence-in-depth so a passed tenantId of Guid.Empty (never matches a real row) cannot
        // leak across tenants. EndedAt > now matches the active-intents snapshot filter so a
        // device_action test fire (future EndedAt) is acknowledgeable during its window.
        var excursion = await db.AlertExcursions
            .FirstOrDefaultAsync(e => e.Id == excursionId
                                       && e.TenantId == tenantId
                                       && (e.EndedAt == null || e.EndedAt > now), ct);

        if (excursion is null)
        {
            logger.LogDebug("Excursion {ExcursionId} not found or already closed; nothing to acknowledge", excursionId);
            return AlertAcknowledgementOutcome.Closed;
        }

        if (excursion.AcknowledgedAt is not null)
        {
            return AlertAcknowledgementOutcome.Acknowledged;
        }

        if (!await AcknowledgesForEveryoneAsync(db.TenantId, caller, ct))
        {
            await MuteAsync(db, excursion, caller.SubjectId!.Value, now, ct);
            return AlertAcknowledgementOutcome.Muted;
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

        return AlertAcknowledgementOutcome.Acknowledged;
    }

    /// <summary>
    /// Whether <paramref name="caller"/> holds <see cref="Scope.AlertsReadWrite"/>, on the
    /// credential or, failing that, on the membership behind it. The membership is resolved as an
    /// interactive login would be, because a device grant is deliberately narrower than its
    /// member and must not demote an owner's acknowledgement to a mute.
    /// </summary>
    private async Task<bool> AcknowledgesForEveryoneAsync(
        Guid tenantId, AlertAcknowledgementAuthority caller, CancellationToken ct)
    {
        if (Scope.Satisfies(caller.GrantedScopes, Scope.AlertsReadWrite))
            return true;

        if (caller.SubjectId is not { } subjectId)
            throw new InvalidOperationException(
                "An acknowledgement without alerts.readwrite needs a subject to mute for.");

        var permissions = await memberService.GetEffectivePermissionsAsync(subjectId, tenantId, ct);
        if (permissions is null)
            return false;

        var membershipScopes = MemberScopeResolver.Resolve(
            permissions, AuthType.SessionCookie, new HashSet<string>());
        return Scope.Satisfies(membershipScopes, Scope.AlertsReadWrite);
    }

    private async Task MuteAsync(
        NocturneDbContext db, AlertExcursionEntity excursion, Guid subjectId, DateTime now, CancellationToken ct)
    {
        var alreadyMuted = await db.AlertExcursionMutes
            .AnyAsync(m => m.AlertExcursionId == excursion.Id && m.SubjectId == subjectId, ct);

        if (!alreadyMuted)
        {
            db.AlertExcursionMutes.Add(new AlertExcursionMuteEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = excursion.TenantId,
                SubjectId = subjectId,
                AlertExcursionId = excursion.Id,
                CreatedAt = now,
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                // A concurrent mute by the same member won the insert; the mute stands either way.
            }
        }

        // Companion devices drop the payload and reconcile against the active-intents snapshot,
        // which now reports this excursion to the subject as acknowledged.
        await broadcastService.BroadcastDeviceActionToSubjectAsync(subjectId, new DeviceActionIntent
        {
            Intent = "acknowledged",
            ExcursionId = excursion.Id,
            Acknowledged = true,
            StartedAt = excursion.StartedAt,
        });

        logger.LogInformation(
            "Subject {SubjectId} muted excursion {ExcursionId} for themselves", subjectId, excursion.Id);
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
