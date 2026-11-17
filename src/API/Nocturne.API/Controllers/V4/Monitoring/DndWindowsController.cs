using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.API.Authorization;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// Do Not Disturb windows that mute <c>lows</c>, <c>highs</c> or <c>all</c> non-critical alerts for a span of time.
/// Alerts still fire and are recorded. Only delivery is muted. Create windows, list the active ones, and clear them.
/// </summary>
/// <remarks>
/// <para>
/// Scoped Do Not Disturb windows (ADR 0004). The orchestrator gate reads the resolved active scopes
/// to suppress delivery. A window's "active" state is computed on read, never stored; rows are
/// retained after clear/expiry for audit.
/// </para>
/// <para>
/// One active window per scope <em>at any instant</em> is enforced by
/// <see cref="IDndWindowRepository.CreateAsync"/>, which reconciles the same-scope windows whose span
/// overlaps the new one. Creates are
/// idempotent by the client-supplied id so a device/web retry never double-applies. A
/// <c>scope=all</c> window is tenant-wide DND and additionally drives the <c>do_not_disturb</c>
/// condition via the enricher.
/// </para>
/// <para>
/// Opening and clearing a window both decide whether alerts are delivered, so they require
/// <see cref="Scope.AlertsReadWrite"/>; the class-level <c>[Authorize]</c> alone is satisfied
/// by read-only credentials such as a guest-link session, which holds <c>alerts.read</c>.
/// </para>
/// </remarks>
/// <seealso cref="AlertRulesController"/>
[ApiController]
[System.ComponentModel.DisplayName("Do Not Disturb")]
[Tags("Monitoring")]
[Authorize]
[Route("api/v4/alerts/dnd/windows")]
[DenyNoAccountViewer]
public class DndWindowsController : ControllerBase
{
    private readonly IDndWindowRepository _windows;

    /// <summary>Initializes a new instance of <see cref="DndWindowsController"/>.</summary>
    public DndWindowsController(IDndWindowRepository windows)
    {
        _windows = windows;
    }

    /// <summary>
    /// List the tenant's DND windows that are active right now (resolved on read).
    /// </summary>
    [HttpGet("active")]
    [RequireScope(Scope.AlertsRead)]
    [RemoteQuery]
    [ProducesResponseType(typeof(List<DndWindowResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<DndWindowResponse>>> GetActive(CancellationToken ct)
    {
        var active = await _windows.GetActiveAsync(scope: null, ct);
        return Ok(active.Select(MapToResponse).ToList());
    }

    /// <summary>
    /// Create (or idempotently return) a DND window. Supersedes any active window of the same
    /// scope so at most one is ever active per scope.
    /// </summary>
    [HttpPost]
    [RequireScope(Scope.AlertsReadWrite)]
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

        var startedAt = request.StartedAt.AsUtc();
        var endsAt = request.EndsAt is { } e ? e.AsUtc() : (DateTime?)null;
        if (endsAt is { } ends && ends <= startedAt)
            return BadRequest("ends_at must be after started_at.");

        var result = await _windows.CreateAsync(
            new NewDndWindow(request.Id, scope, startedAt, endsAt, request.Source), ct);

        return result switch
        {
            { Outcome: DndWindowCreateOutcome.Created, Window: { } created } =>
                CreatedAtAction(nameof(GetActive), MapToResponse(created)),
            { Window: { } stored } => Ok(MapToResponse(stored)),
            _ => Conflict("window id is already in use."),
        };
    }

    /// <summary>
    /// Clear a DND window (turn the mute off). Idempotent: a window already cleared (by the user
    /// or by supersede) is returned unchanged so a retry never rewrites the audit timestamp.
    /// </summary>
    [HttpPost("{id:guid}/clear")]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand(Invalidates = ["GetActive"])]
    [ProducesResponseType(typeof(DndWindowResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DndWindowResponse>> Clear(Guid id, CancellationToken ct)
    {
        var window = await _windows.ClearAsync(id, ct);
        return window is null ? NotFound() : Ok(MapToResponse(window));
    }

    private static DndWindowResponse MapToResponse(DndWindowRecord w) => new()
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
