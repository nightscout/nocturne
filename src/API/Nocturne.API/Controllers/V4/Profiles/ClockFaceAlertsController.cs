using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Controllers.V4.Profiles;

/// <summary>
/// The alarms a public clock face shows, for a face whose owner turned on
/// <see cref="ClockSettings.ShowAlerts"/>.
/// </summary>
/// <remarks>
/// The clock id is the capability, as for <see cref="ClockFacesController.GetGlucose"/>: a caller
/// holding a valid id for the host's tenant gets what the face is set to show, read under tenant
/// RLS as a reader who is not a share. The face serves alerts only while its owner may still read
/// them, so a member whose alert access is taken away cannot keep it through a face made earlier.
/// </remarks>
[ApiController]
[Tags("Profiles")]
[Route("api/v4/clockfaces/{id:guid}/alerts")]
[AllowAnonymous]
[EnableRateLimiting(ServiceRegistrationExtensions.NoAccountAlertsRateLimitPolicy)]
public class ClockFaceAlertsController(
    IClockFaceService clockFaces,
    INocturneDbContextFactory contexts,
    AlertReceiptService receipts,
    ITenantMemberService members,
    ITenantAccessor tenant) : ControllerBase
{
    /// <summary>The alarms firing now, or none when the face does not show alerts.</summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(ClockAlertDto[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClockAlertDto[]>> GetClockAlerts(Guid id)
    {
        var ct = HttpContext.RequestAborted;
        var face = await clockFaces.GetByIdAsync(id, ct);
        if (face is null)
            return NotFound();

        if (!face.Config.Settings.ShowAlerts || !await OwnerReadsAlertsAsync(face, ct))
            return Ok(Array.Empty<ClockAlertDto>());

        await using var db = await contexts.CreateAsync(ct);
        var now = DateTime.UtcNow;
        var excursions = await db.AlertExcursions
            .AsNoTracking()
            .Include(e => e.AlertRule)
            .Include(e => e.Instances)
            .Where(e => e.EndedAt == null)
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync(ct);
        var seen = await AlertReceiptService.ReadAsync(
            db, excursions.Select(e => e.Id).ToList(), Actor(face), ct);

        return Ok(excursions.Select(e => new ClockAlertDto
        {
            Id = e.Id,
            RuleName = e.AlertRule?.Name ?? string.Empty,
            Severity = e.AlertRule?.Severity ?? AlertRuleSeverity.Warning,
            ConditionType = e.AlertRule?.ConditionType ?? AlertConditionType.Threshold,
            StartedAt = e.StartedAt,
            Acknowledged = e.AcknowledgedAt is not null,
            SeenHere = seen[e.Id].Any(),
            SnoozedUntil = e.Instances
                .Where(i => i.ResolvedAt == null)
                .Select(i => AlertSnooze.ActiveUntil(i.SnoozedUntil, now))
                .Max(),
        }).ToArray());
    }

    /// <summary>
    /// Record that someone at the clock saw an alarm that is firing, for a face whose owner set
    /// <see cref="ClockSettings.AllowMarkSeen"/>. It acknowledges and mutes nothing; members see
    /// that the face saw it.
    /// </summary>
    [HttpPost("{excursionId:guid}/seen")]
    [RemoteCommand(Invalidates = [nameof(GetClockAlerts)])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> MarkClockAlertSeen(Guid id, Guid excursionId)
    {
        var ct = HttpContext.RequestAborted;
        var face = await clockFaces.GetByIdAsync(id, ct);
        if (face is null || !face.Config.Settings.ShowAlerts || !face.Config.Settings.AllowMarkSeen
            || !await OwnerReadsAlertsAsync(face, ct))
            return NotFound();

        return await receipts.RecordAsync(excursionId, Actor(face), ct) ? NoContent() : NotFound();
    }

    private async Task<bool> OwnerReadsAlertsAsync(ClockFace face, CancellationToken ct)
    {
        if (!Guid.TryParse(face.UserId, out var owner)
            || await members.GetMemberAccessAsync(owner, tenant.TenantId, ct) is not { } access)
            return false;

        var scopes = MemberScopeResolver.Resolve(
            access.EffectivePermissions, AuthType.SessionCookie, new HashSet<string>());
        return Scope.Satisfies(scopes, Scope.AlertsRead);
    }

    private static AlertReceiptActor Actor(ClockFace face) =>
        new(AlertReceiptActorKind.Clock, face.Id, face.Name);
}
