using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// Scoped Do Not Disturb windows (ADR 0004): a user mutes <c>lows</c> / <c>highs</c> / <c>all</c>
/// non-critical alerts for a span of time. The alert still fires and is recorded — only delivery
/// is suppressed (the orchestrator gate reads the resolved active scopes). A window's "active"
/// state is computed on read, never stored; rows are retained after clear/expiry for audit.
/// </summary>
/// <remarks>
/// One active window per scope <em>at any instant</em> is enforced here: creating a window
/// reconciles the same-scope windows whose span overlaps it — an earlier one is truncated to end
/// where the new one begins, one it wholly covers is cleared
/// (<c>cleared_by = "system:superseded"</c>), and a non-overlapping one is left alone. Creates are
/// idempotent by the client-supplied id so a device/web retry never double-applies. A
/// <c>scope=all</c> window is tenant-wide DND and additionally drives the <c>do_not_disturb</c>
/// condition via the enricher.
/// </remarks>
/// <seealso cref="AlertRulesController"/>
[ApiController]
[Tags("Monitoring")]
[Authorize]
[Route("api/v4/alerts/dnd/windows")]
public class DndWindowsController : ControllerBase
{
    /// <summary>Marker stamped on a window cleared because a newer window of the same scope superseded it.</summary>
    public const string SupersededBy = "system:superseded";

    private readonly ITenantDbContextFactory _contextFactory;

    /// <summary>Initializes a new instance of <see cref="DndWindowsController"/>.</summary>
    public DndWindowsController(ITenantDbContextFactory contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// List the tenant's DND windows that are active right now (resolved on read).
    /// </summary>
    [HttpGet("active")]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<DndWindowResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<DndWindowResponse>>> GetActive(CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateAsync(ct);
        var now = DateTime.UtcNow;

        var active = await db.DndWindows
            .AsNoTracking()
            .Where(w => w.ClearedAt == null && w.StartedAt <= now && (w.EndsAt == null || now < w.EndsAt))
            .OrderBy(w => w.StartedAt)
            .ToListAsync(ct);

        return Ok(active.Select(MapToResponse).ToList());
    }

    /// <summary>
    /// Create (or idempotently return) a DND window. Supersedes any active window of the same
    /// scope so at most one is ever active per scope.
    /// </summary>
    [HttpPost]
    [RemoteCommand(Invalidates = ["GetActive"])]
    [ProducesResponseType(typeof(DndWindowResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(DndWindowResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DndWindowResponse>> Create(
        [FromBody] CreateDndWindowRequest request, CancellationToken ct)
    {
        if (request.Id == Guid.Empty)
            return BadRequest("A client-supplied window id is required.");

        // Scope is nullable on the request so an omitted field is rejected rather than
        // silently defaulting to the zero enum member (lows) — muting the wrong class of
        // alert is not a reasonable reading of a malformed request.
        if (request.Scope is not { } scope)
            return BadRequest("scope is required (lows | highs | all).");

        var startedAt = AsUtc(request.StartedAt);
        var endsAt = request.EndsAt is { } e ? AsUtc(e) : (DateTime?)null;
        if (endsAt is { } ends && ends <= startedAt)
            return BadRequest("ends_at must be after started_at.");

        await using var db = await _contextFactory.CreateAsync(ct);
        var tenantId = db.TenantId;

        // Idempotent by client id: a retry returns the stored window unchanged (no re-supersede).
        var existing = await db.DndWindows
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == request.Id, ct);
        if (existing is not null)
            return Ok(MapToResponse(existing));

        // Keep "at most one window of a scope active at any instant" by reconciling against the
        // *span* of the incoming window rather than against what happens to be active right now.
        //
        // Only same-scope windows whose span overlaps the new one are touched. A window that
        // merely expired, or one scheduled for a later span that this one does not reach, is left
        // exactly as it is — restamping the first would falsify the audit trail these rows are
        // retained for, and clearing the second would destroy standing user intent that adding
        // this mute said nothing about.
        //
        // Of the overlapping ones:
        //  - one that started earlier is *truncated* to end where this one begins, so a running
        //    mute is handed over at the boundary instead of being switched off;
        //  - one that starts at or after this window's start is wholly covered, so it is cleared.
        //
        // A null EndsAt is an open-ended span, so [started, ∞).
        var now = DateTime.UtcNow;
        var overlapping = await db.DndWindows
            .Where(w => w.Scope == scope && w.ClearedAt == null
                        && (w.EndsAt == null || w.EndsAt > startedAt)
                        && (endsAt == null || w.StartedAt < endsAt))
            .ToListAsync(ct);
        foreach (var w in overlapping)
        {
            if (w.StartedAt < startedAt)
            {
                w.EndsAt = startedAt;
            }
            else
            {
                w.ClearedAt = now;
                w.ClearedBy = SupersededBy;
            }
        }

        var window = new DndWindowEntity
        {
            Id = request.Id,
            TenantId = tenantId,
            Scope = scope,
            StartedAt = startedAt,
            EndsAt = endsAt,
            Source = request.Source,
            // CreatedAt is the server receipt time, set by the DB default (CURRENT_TIMESTAMP).
        };
        db.DndWindows.Add(window);
        try
        {
            // CreatedAt (DB default) is populated on the tracked entity by INSERT ... RETURNING.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // dnd_windows.id is the global PK, but the idempotency lookup above runs under
            // the tenant query filter (and RLS), so an id owned by another tenant is
            // invisible and the insert hits the unique constraint. Re-check under this
            // tenant: a concurrent same-tenant retry resolves idempotently; otherwise the
            // id belongs to another tenant and this insert can never succeed.
            var raced = await db.DndWindows
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == request.Id, ct);
            if (raced is not null)
                return Ok(MapToResponse(raced));
            return Conflict("window id is already in use.");
        }

        return CreatedAtAction(nameof(GetActive), MapToResponse(window));
    }

    /// <summary>
    /// Clear a DND window (turn the mute off). Idempotent: a window already cleared (by the user
    /// or by supersede) is returned unchanged so a retry never rewrites the audit timestamp.
    /// </summary>
    [HttpPost("{id:guid}/clear")]
    [RemoteCommand(Invalidates = ["GetActive"])]
    [ProducesResponseType(typeof(DndWindowResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DndWindowResponse>> Clear(Guid id, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateAsync(ct);

        var window = await db.DndWindows.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (window is null)
            return NotFound();

        // First-writer-wins: don't clobber an existing clear (user-clear or supersede).
        if (window.ClearedAt is null)
        {
            window.ClearedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return Ok(MapToResponse(window));
    }

    private static DndWindowResponse MapToResponse(DndWindowEntity w) => new()
    {
        Id = w.Id,
        Scope = w.Scope,
        StartedAt = w.StartedAt,
        EndsAt = w.EndsAt,
        ClearedAt = w.ClearedAt,
        ClearedBy = w.ClearedBy,
        Source = w.Source,
        CreatedAt = w.CreatedAt,
    };

    /// <summary>Normalises an incoming instant to UTC (the windows columns are UTC instants).</summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}

#region DTOs

/// <summary>Request to create a DND window. <see cref="Id"/> is client-supplied for idempotency.</summary>
public class CreateDndWindowRequest
{
    /// <summary>Client-generated UUID. Re-sending the same id is a no-op that returns the stored window.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Which alerts to silence: <c>lows</c> | <c>highs</c> | <c>all</c>. Required — nullable so
    /// an omitted value is a 400 rather than defaulting to the zero enum member.
    /// </summary>
    public DndScope? Scope { get; set; }

    /// <summary>When the mute takes effect (may predate receipt for offline authoring).</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Auto-expiry instant; null mutes until explicitly cleared.</summary>
    public DateTime? EndsAt { get; set; }

    /// <summary>Audit label for what created the window (e.g. <c>web</c>, <c>prelude-device</c>).</summary>
    [MaxLength(128)]
    public string? Source { get; set; }
}

/// <summary>A DND window with its resolver fields. <c>active</c> state is computed on read.</summary>
public class DndWindowResponse
{
    public Guid Id { get; set; }
    public DndScope Scope { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public DateTime? ClearedAt { get; set; }
    public string? ClearedBy { get; set; }
    public string? Source { get; set; }
    public DateTime CreatedAt { get; set; }
}

#endregion
