using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// Tenant-level alert configuration: Do Not Disturb (manual toggle with optional
/// auto-expire and a recurring scheduled window) and the IANA timezone the schedule
/// is interpreted in. The row is created lazily on first access.
/// </summary>
/// <remarks>
/// DND has two activation paths that share the same allowlist semantics — the
/// per-rule <see cref="AlertRuleEntity.AllowThroughDnd"/> bypass applies to both,
/// and critical rules implicitly bypass DND regardless. Power users can also
/// reference DND inside a rule tree via the <c>do_not_disturb</c> condition.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/v4/tenant-alert-settings")]
[Tags("Monitoring")]
public class TenantAlertSettingsController : ControllerBase
{
    private readonly IDbContextFactory<NocturneDbContext> _contextFactory;

    public TenantAlertSettingsController(IDbContextFactory<NocturneDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Get the current tenant's alert settings, creating a default row if one does not exist.
    /// </summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(TenantAlertSettingsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantAlertSettingsResponse>> Get(CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var entity = await db.TenantAlertSettings.FirstOrDefaultAsync(ct);
        if (entity is null)
        {
            entity = new TenantAlertSettingsEntity { TenantId = db.TenantId };
            db.TenantAlertSettings.Add(entity);
            await db.SaveChangesAsync(ct);
        }

        return Ok(MapToResponse(entity));
    }

    /// <summary>
    /// Replace the current tenant's alert settings. Upserts on first call.
    /// </summary>
    [HttpPut]
    [RemoteCommand(Invalidates = ["Get"])]
    [ProducesResponseType(typeof(TenantAlertSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TenantAlertSettingsResponse>> Update(
        [FromBody] UpdateTenantAlertSettingsRequest request, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var entity = await db.TenantAlertSettings.FirstOrDefaultAsync(ct);
        var isNew = entity is null;
        entity ??= new TenantAlertSettingsEntity { TenantId = db.TenantId };

        // Anchor the manual-DND-started timestamp on the transition off→on so sustained
        // do_not_disturb conditions (`for_minutes`) measure from the activation moment.
        if (request.DndManualActive && !entity.DndManualActive)
        {
            entity.DndManualStartedAt = DateTime.UtcNow;
        }
        else if (!request.DndManualActive)
        {
            entity.DndManualStartedAt = null;
        }

        entity.DndManualActive = request.DndManualActive;
        entity.DndManualUntil = request.DndManualUntil;
        entity.DndScheduleEnabled = request.DndScheduleEnabled;
        entity.DndScheduleStart = request.DndScheduleStart;
        entity.DndScheduleEnd = request.DndScheduleEnd;
        entity.Timezone = string.IsNullOrWhiteSpace(request.Timezone) ? "UTC" : request.Timezone;
        entity.UpdatedAt = DateTime.UtcNow;

        if (isNew) db.TenantAlertSettings.Add(entity);
        await db.SaveChangesAsync(ct);

        return Ok(MapToResponse(entity));
    }

    private static TenantAlertSettingsResponse MapToResponse(TenantAlertSettingsEntity e) => new()
    {
        DndManualActive = e.DndManualActive,
        DndManualUntil = e.DndManualUntil,
        DndManualStartedAt = e.DndManualStartedAt,
        DndScheduleEnabled = e.DndScheduleEnabled,
        DndScheduleStart = e.DndScheduleStart,
        DndScheduleEnd = e.DndScheduleEnd,
        Timezone = e.Timezone,
    };
}

#region DTOs

public class TenantAlertSettingsResponse
{
    /// <summary>True when the user has manually toggled DND on.</summary>
    public bool DndManualActive { get; set; }

    /// <summary>UTC instant at which a manually-activated DND auto-expires. Null = indefinite.</summary>
    public DateTime? DndManualUntil { get; set; }

    /// <summary>UTC instant at which DND was most recently activated. Anchors sustained
    /// <c>do_not_disturb</c> conditions.</summary>
    public DateTime? DndManualStartedAt { get; set; }

    /// <summary>True when a recurring scheduled DND window is configured.</summary>
    public bool DndScheduleEnabled { get; set; }

    /// <summary>Local-time start of the scheduled DND window (in <see cref="Timezone"/>).</summary>
    public TimeOnly? DndScheduleStart { get; set; }

    /// <summary>Local-time end of the scheduled DND window. Cross-midnight windows allowed.</summary>
    public TimeOnly? DndScheduleEnd { get; set; }

    /// <summary>IANA timezone (e.g. <c>Europe/London</c>) for the scheduled window.</summary>
    public string Timezone { get; set; } = "UTC";
}

public class UpdateTenantAlertSettingsRequest
{
    public bool DndManualActive { get; set; }
    public DateTime? DndManualUntil { get; set; }
    public bool DndScheduleEnabled { get; set; }
    public TimeOnly? DndScheduleStart { get; set; }
    public TimeOnly? DndScheduleEnd { get; set; }
    public string Timezone { get; set; } = "UTC";
}

#endregion
