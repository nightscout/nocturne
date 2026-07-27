using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Timezones;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Timezones;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Manages the tenant's timezone timeline — the ordered record of which IANA zone the person was in
/// over time. Connectors that store local-wall-clock-stamped-as-UTC data (e.g. Glooko) use it to
/// convert timestamps to true UTC, honouring daylight saving and travel/relocation.
/// </summary>
/// <seealso cref="ITimezoneTimelineService"/>
[ApiController]
[Route("api/v4/timezone-timeline")]
[Tags("Timezone Timeline")]
[Authorize]
public class TimezoneTimelineController : ControllerBase, IWriteScopedController
{
    /// <summary>
    /// The OAuth scope every write action on this controller requires. The timeline is patient
    /// clinical configuration in the same sense as the timezone on <c>patient_records</c>, which
    /// <c>PatientRecordController</c> gates on <c>therapy.readwrite</c>, and V1/V3 profile writes
    /// require the same scope. <see cref="Recorrect"/> additionally triggers a Glooko re-import; the
    /// records it rewrites are written by the connector's own publish path, so the scope here bounds
    /// who may request the re-correction, not what the connector may write. The class-level
    /// <c>[Authorize]</c> alone is satisfied by read-only credentials such as a guest-link session.
    /// </summary>
    public string WriteScope => OAuthScopes.TherapyReadWrite;

    private const string GlookoConnectorId = "glooko";

    private readonly ITimezoneTimelineService _timeline;
    private readonly IConnectorSyncService _syncService;
    private readonly ILogger<TimezoneTimelineController> _logger;

    public TimezoneTimelineController(
        ITimezoneTimelineService timeline,
        IConnectorSyncService syncService,
        ILogger<TimezoneTimelineController> logger)
    {
        _timeline = timeline;
        _syncService = syncService;
        _logger = logger;
    }

    /// <summary>Get the tenant's timezone timeline, ordered by effective date.</summary>
    [HttpGet]
    [RemoteQuery]
    public async Task<ActionResult<IReadOnlyList<TimezoneTimelineEntry>>> GetTimeline(CancellationToken cancellationToken)
    {
        var entries = await _timeline.GetTimelineAsync(cancellationToken);
        return Ok(entries);
    }

    /// <summary>
    /// Create or update a timeline entry. <c>EffectiveFrom</c> is a local wall-clock date/time (the
    /// moment, in local terms, that the zone took effect). A trip is two entries (out + return); a
    /// move is a single entry; the origin entry covers all earlier history.
    /// </summary>
    [HttpPut]
    [RequireDeclaredWriteScope]
    [RemoteCommand(Invalidates = ["GetTimeline"])]
    public async Task<ActionResult<TimezoneTimelineEntry>> Upsert(
        [FromBody] UpsertTimezoneEntryRequest request,
        CancellationToken cancellationToken)
    {
        var saved = await _timeline.UpsertAsync(
            new TimezoneTimelineEntry
            {
                Id = request.Id ?? Guid.Empty,
                EffectiveFrom = request.EffectiveFrom,
                Timezone = request.Timezone,
            },
            cancellationToken);

        return Ok(saved);
    }

    /// <summary>Delete a timeline entry.</summary>
    [HttpDelete("{id:guid}")]
    [RequireDeclaredWriteScope]
    [RemoteCommand(Invalidates = ["GetTimeline"])]
    public async Task<ActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await _timeline.DeleteAsync(id, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>
    /// Re-correct already-imported data after a timeline change by re-pulling the affected window from
    /// Glooko. Records re-flow the normal publish path and upsert in place on their stable
    /// SyncIdentifier, so timestamps move without duplicating. Intended to be called after the user
    /// confirms the affected window. Runs synchronously; the window is bounded.
    /// </summary>
    /// <param name="request">Optional lower bound (UTC). When null, the connector's default window is used.</param>
    [HttpPost("recorrect")]
    [RequireDeclaredWriteScope]
    [RemoteCommand]
    public async Task<ActionResult<RecorrectResult>> Recorrect(
        [FromBody] RecorrectRequest request,
        CancellationToken cancellationToken)
    {
        var syncRequest = new SyncRequest
        {
            From = request.From,
            To = DateTime.UtcNow,
            DataTypes = [],
        };

        _logger.LogInformation(
            "Timezone re-correction requested: re-syncing Glooko from {From} to now", request.From?.ToString("o") ?? "default");

        var result = await _syncService.TriggerSyncAsync(GlookoConnectorId, syncRequest, cancellationToken);
        return Ok(new RecorrectResult(result.Success, result.Message));
    }
}

/// <summary>Request to create or update a timezone timeline entry.</summary>
/// <param name="Id">Existing entry id to update, or null to create.</param>
/// <param name="EffectiveFrom">Local wall-clock instant from which the zone takes effect.</param>
/// <param name="Timezone">IANA timezone id (e.g. "Australia/Sydney").</param>
public record UpsertTimezoneEntryRequest(Guid? Id, DateTime EffectiveFrom, string Timezone);

/// <summary>Request to re-correct imported data after a timeline change.</summary>
/// <param name="From">Optional UTC lower bound for the re-pull window.</param>
public record RecorrectRequest(DateTime? From);

/// <summary>Outcome of a re-correction re-sync.</summary>
/// <param name="Success">Whether the re-sync succeeded.</param>
/// <param name="Message">Human-readable status message.</param>
public record RecorrectResult(bool Success, string Message);
