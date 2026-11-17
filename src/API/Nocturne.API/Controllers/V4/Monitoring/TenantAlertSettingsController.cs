using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.API.Authorization;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// Tenant-wide alert settings, including Do Not Disturb. Read the settings, and update the manual
/// Do Not Disturb toggle and its recurring schedule. Scheduled times use the patient's timezone.
/// </summary>
/// <remarks>
/// The schedule is interpreted in the patient's timezone (<see cref="Nocturne.Core.Models.V4.PatientRecord.Timezone"/>),
/// which is set there, not here. The row is created lazily on first access.
/// The request/response shape is unchanged, but manual DND is now backed by a
/// <c>scope=all</c> row in <c>dnd_windows</c> (ADR 0004 D5) rather than columns on this row:
/// the toggle creates/clears that window and <c>dndManual*</c> is computed from it. Scheduled
/// DND stays here. The per-rule <see cref="AlertRuleSnapshot.AllowThroughDnd"/> bypass and the
/// <c>do_not_disturb</c> condition apply to both paths; critical rules implicitly bypass DND.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/v4/tenant-alert-settings")]
[DenyNoAccountViewer]
[Tags("Monitoring")]
public class TenantAlertSettingsController : ControllerBase
{
    private readonly ITenantAlertSettingsRepository _settings;
    private readonly IDndWindowRepository _windows;
    private readonly ITherapySettingsResolver _therapySettings;
    private readonly TimeProvider _timeProvider;

    public TenantAlertSettingsController(
        ITenantAlertSettingsRepository settings,
        IDndWindowRepository windows,
        ITherapySettingsResolver therapySettings,
        TimeProvider timeProvider)
    {
        _settings = settings;
        _windows = windows;
        _therapySettings = therapySettings;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Get the current tenant's alert settings, creating a default row if one does not exist.
    /// Read-only credentials may call it; a repeat call stores nothing further.
    /// </summary>
    /// <remarks>
    /// The get-or-create is a write, but of server-defined defaults and idempotently, so it carries
    /// no <see cref="Scope.AlertsReadWrite"/> gate: a read-only caller cannot choose what it
    /// stores and a repeat call stores nothing further.
    /// </remarks>
    [HttpGet]
    [RequireScope(Scope.AlertsRead)]
    [RemoteQuery]
    [ProducesResponseType(typeof(TenantAlertSettingsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantAlertSettingsResponse>> Get(CancellationToken ct)
    {
        var settings = await _settings.GetOrCreateAsync(ct);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var manual = await ActiveManualWindowAsync(ct);
        return Ok(MapToResponse(settings, manual, await ScheduleActiveNowAsync(settings, now, ct)));
    }

    /// <summary>
    /// Replace the current tenant's alert settings. Upserts on first call. The manual-DND toggle
    /// creates/clears the tenant's <c>scope=all</c> window; scheduled fields persist on the row.
    /// Needs <c>alerts.readwrite</c>: the toggle suppresses delivery of every non-critical alert
    /// tenant-wide, and read-only credentials such as a guest-link session are refused.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="Scope.AlertsReadWrite"/>: this toggles tenant-wide Do not disturb,
    /// which suppresses delivery of every non-critical alert. The class-level <c>[Authorize]</c>
    /// alone is satisfied by read-only credentials such as a guest-link session, which holds
    /// <c>alerts.read</c>.
    /// </remarks>
    [HttpPut]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand(Invalidates = ["Get"])]
    [ProducesResponseType(typeof(TenantAlertSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TenantAlertSettingsResponse>> Update(
        [FromBody] UpdateTenantAlertSettingsRequest request, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // A manual mute must end in the future (mirrors DndWindowsController's ends_at
        // guard): a past until would persist a never-active window (EndsAt <= StartedAt)
        // — or retroactively expire the kept one without an audit clear — and the
        // response would report DndManualActive=false despite the request saying true.
        // Only when the request is actually turning the mute on: with the toggle absent the
        // expiry is never read, so rejecting a stale echo of it would 400 a schedule-only save.
        if (request.DndManualActive == true && request.DndManualUntil?.AsUtc() is { } until && until <= now)
            return BadRequest("dnd_manual_until must be in the future.");

        var settings = new TenantAlertSettingsSnapshot(
            request.DndScheduleEnabled, request.DndScheduleStart, request.DndScheduleEnd);

        // Manual DND is a scope=all window in a table this controller shares with
        // DndWindowsController (which devices post to directly). An absent dndManualActive
        // therefore means "leave the mute alone", not false: treating it as false would make
        // saving a *schedule* change silently cancel a mute a device had taken out, and would
        // rewrite that window's expiry on the way through.
        var manual = request.DndManualActive is { } manualActive
            ? new ManualDndToggle(manualActive, request.DndManualUntil?.AsUtc())
            : null;

        var scheduleActiveNow = await ScheduleActiveNowAsync(settings, now, ct);

        await _settings.UpdateAsync(settings, manual, ct);

        return Ok(MapToResponse(settings, await ActiveManualWindowAsync(ct), scheduleActiveNow));
    }

    /// <summary>The representative active manual (scope=all) window — earliest started — or null.</summary>
    private async Task<DndWindowRecord?> ActiveManualWindowAsync(CancellationToken ct) =>
        (await _windows.GetActiveAsync(DndScope.All, ct)).FirstOrDefault();

    private async Task<bool> ScheduleActiveNowAsync(
        TenantAlertSettingsSnapshot schedule, DateTime now, CancellationToken ct)
    {
        if (!schedule.DndScheduleEnabled)
            return false;

        return schedule.Resolve(now, await _therapySettings.GetTimezoneAsync(ct: ct)) is not null;
    }

    private static TenantAlertSettingsResponse MapToResponse(
        TenantAlertSettingsSnapshot e, DndWindowRecord? manual, bool scheduleActiveNow) => new()
    {
        DndManualActive = manual is not null,
        DndManualUntil = manual?.EndsAt,
        DndManualStartedAt = manual?.StartedAt,
        DndScheduleEnabled = e.DndScheduleEnabled,
        DndScheduleStart = e.DndScheduleStart,
        DndScheduleEnd = e.DndScheduleEnd,
        DndScheduleActiveNow = scheduleActiveNow,
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

    /// <summary>True when the scheduled window is in effect at this instant, evaluated in the
    /// patient's timezone by <see cref="TenantAlertSettingsSnapshot.Resolve"/>, the rule the alert
    /// engine suppresses by.</summary>
    public bool DndScheduleActiveNow { get; set; }

    /// <summary>Local-time start of the scheduled DND window, interpreted in the patient's timezone.</summary>
    public TimeOnly? DndScheduleStart { get; set; }

    /// <summary>Local-time end of the scheduled DND window. Cross-midnight windows allowed.</summary>
    public TimeOnly? DndScheduleEnd { get; set; }
}

public class UpdateTenantAlertSettingsRequest
{
    /// <summary>
    /// Manual (scope=all) DND toggle. Omit to leave the tenant's manual mute untouched — the
    /// mute lives in <c>dnd_windows</c>, which devices write to independently, so a settings
    /// save that only changes the schedule must not disturb it.
    /// </summary>
    public bool? DndManualActive { get; set; }
    public DateTime? DndManualUntil { get; set; }
    public bool DndScheduleEnabled { get; set; }
    public TimeOnly? DndScheduleStart { get; set; }
    public TimeOnly? DndScheduleEnd { get; set; }
}

#endregion
