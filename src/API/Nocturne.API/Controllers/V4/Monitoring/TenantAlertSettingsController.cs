using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// Tenant-level alert configuration: Do Not Disturb (manual toggle with optional
/// auto-expire and a recurring scheduled window). The schedule is interpreted in the
/// patient's timezone (<see cref="V4.PatientRecord.Timezone"/>) — set there, not here.
/// The row is created lazily on first access.
/// </summary>
/// <remarks>
/// The request/response shape is unchanged, but manual DND is now backed by a
/// <c>scope=all</c> row in <c>dnd_windows</c> (ADR 0004 D5) rather than columns on this row:
/// the toggle creates/clears that window and <c>dndManual*</c> is computed from it. Scheduled
/// DND stays here. The per-rule <see cref="AlertRuleEntity.AllowThroughDnd"/> bypass and the
/// <c>do_not_disturb</c> condition apply to both paths; critical rules implicitly bypass DND.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/v4/tenant-alert-settings")]
[Tags("Monitoring")]
public class TenantAlertSettingsController : ControllerBase
{
    private const string ManualSource = "web";

    private readonly ITenantDbContextFactory _contextFactory;

    public TenantAlertSettingsController(ITenantDbContextFactory contextFactory)
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
        await using var db = await _contextFactory.CreateAsync(ct);

        var entity = await db.TenantAlertSettings.FirstOrDefaultAsync(ct);
        if (entity is null)
        {
            entity = new TenantAlertSettingsEntity { TenantId = db.TenantId };
            db.TenantAlertSettings.Add(entity);
            await db.SaveChangesAsync(ct);
        }

        var manual = await ActiveManualWindowAsync(db, DateTime.UtcNow, ct);
        return Ok(MapToResponse(entity, manual));
    }

    /// <summary>
    /// Replace the current tenant's alert settings. Upserts on first call. The manual-DND toggle
    /// creates/clears the tenant's <c>scope=all</c> window; scheduled fields persist on the row.
    /// </summary>
    [HttpPut]
    [RemoteCommand(Invalidates = ["Get"])]
    [ProducesResponseType(typeof(TenantAlertSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TenantAlertSettingsResponse>> Update(
        [FromBody] UpdateTenantAlertSettingsRequest request, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateAsync(ct);
        var now = DateTime.UtcNow;

        // A manual mute must end in the future (mirrors DndWindowsController's ends_at
        // guard): a past until would persist a never-active window (EndsAt <= StartedAt)
        // — or retroactively expire the kept one without an audit clear — and the
        // response would report DndManualActive=false despite the request saying true.
        if (request.DndManualActive && AsUtc(request.DndManualUntil) is { } until && until <= now)
            return BadRequest("dnd_manual_until must be in the future.");

        var entity = await db.TenantAlertSettings.FirstOrDefaultAsync(ct);
        var isNew = entity is null;
        entity ??= new TenantAlertSettingsEntity { TenantId = db.TenantId };

        entity.DndScheduleEnabled = request.DndScheduleEnabled;
        entity.DndScheduleStart = request.DndScheduleStart;
        entity.DndScheduleEnd = request.DndScheduleEnd;
        entity.UpdatedAt = now;
        if (isNew) db.TenantAlertSettings.Add(entity);

        // Manual DND is a scope=all window. Toggling on ensures exactly one active all-window
        // (anchoring StartedAt on the existing one so a re-PUT doesn't reset the for_minutes
        // anchor); toggling off clears every uncleared all-window (user-clear, cleared_by
        // null) — including future-started ones, so an explicit "DND off" also cancels a
        // pending mute instead of letting it silently activate later (Create's supersede
        // clears the same set).
        if (request.DndManualActive)
        {
            var activeAll = await ActiveAllWindowsAsync(db, now, ct);
            var endsAt = AsUtc(request.DndManualUntil);
            if (activeAll.Count == 0)
            {
                db.DndWindows.Add(new DndWindowEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = db.TenantId,
                    Scope = DndScope.All,
                    StartedAt = now,
                    EndsAt = endsAt,
                    Source = ManualSource,
                });
            }
            else
            {
                var keep = activeAll.OrderBy(w => w.StartedAt).First();
                keep.EndsAt = endsAt;
                foreach (var extra in activeAll.Where(w => !ReferenceEquals(w, keep)))
                {
                    extra.ClearedAt = now;
                    extra.ClearedBy = DndWindowsController.SupersededBy;
                }
            }
        }
        else
        {
            var uncleared = await db.DndWindows
                .Where(w => w.Scope == DndScope.All && w.ClearedAt == null)
                .ToListAsync(ct);
            foreach (var w in uncleared)
                w.ClearedAt = now;
        }

        await db.SaveChangesAsync(ct);

        var manual = await ActiveManualWindowAsync(db, now, ct);
        return Ok(MapToResponse(entity, manual));
    }

    /// <summary>The active <c>scope=all</c> windows (resolve-on-read), tracked for mutation.</summary>
    private static Task<List<DndWindowEntity>> ActiveAllWindowsAsync(
        NocturneDbContext db, DateTime now, CancellationToken ct) =>
        db.DndWindows
            .Where(w => w.Scope == DndScope.All && w.ClearedAt == null
                        && w.StartedAt <= now && (w.EndsAt == null || now < w.EndsAt))
            .ToListAsync(ct);

    /// <summary>The representative active manual (scope=all) window — earliest started — or null.</summary>
    private static async Task<DndWindowEntity?> ActiveManualWindowAsync(
        NocturneDbContext db, DateTime now, CancellationToken ct) =>
        await db.DndWindows
            .AsNoTracking()
            .Where(w => w.Scope == DndScope.All && w.ClearedAt == null
                        && w.StartedAt <= now && (w.EndsAt == null || now < w.EndsAt))
            .OrderBy(w => w.StartedAt)
            .FirstOrDefaultAsync(ct);

    private static DateTime? AsUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } v => v,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        { } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
    };

    private static TenantAlertSettingsResponse MapToResponse(
        TenantAlertSettingsEntity e, DndWindowEntity? manual) => new()
    {
        DndManualActive = manual is not null,
        DndManualUntil = manual?.EndsAt,
        DndManualStartedAt = manual?.StartedAt,
        DndScheduleEnabled = e.DndScheduleEnabled,
        DndScheduleStart = e.DndScheduleStart,
        DndScheduleEnd = e.DndScheduleEnd,
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

    /// <summary>Local-time start of the scheduled DND window, interpreted in the patient's timezone.</summary>
    public TimeOnly? DndScheduleStart { get; set; }

    /// <summary>Local-time end of the scheduled DND window. Cross-midnight windows allowed.</summary>
    public TimeOnly? DndScheduleEnd { get; set; }
}

public class UpdateTenantAlertSettingsRequest
{
    public bool DndManualActive { get; set; }
    public DateTime? DndManualUntil { get; set; }
    public bool DndScheduleEnabled { get; set; }
    public TimeOnly? DndScheduleStart { get; set; }
    public TimeOnly? DndScheduleEnd { get; set; }
}

#endregion
