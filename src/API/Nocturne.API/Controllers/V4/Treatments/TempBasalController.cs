using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.API.Services.Caching;

namespace Nocturne.API.Controllers.V4.Treatments;

/// <summary>
/// Controller for temporary basal rate spans.
/// Provides read access plus a bulk write endpoint for native uploaders (e.g. Trio) —
/// previously temp basals were only written by the legacy v1 treatment decomposer and connectors.
/// </summary>
/// <remarks>
/// Temp basals are span records (<see cref="TempBasal.StartTimestamp"/>/<see cref="TempBasal.EndTimestamp"/>),
/// not point events. The write endpoint accepts both new spans and cancels: a cancel truncates the
/// span active at its timestamp. Overlapping spans are not auto-truncated on create — the basal
/// timeline resolves overlaps at read time, matching the decomposer-written data.
/// </remarks>
/// <seealso cref="ITempBasalRepository"/>
/// <seealso cref="TempBasal"/>
/// <seealso cref="CreateTempBasalRequest"/>
[ApiController]
[Tags("Treatments")]
[Route("api/v4/insulin/temp-basals")]
[Produces("application/json")]
[ClearsTenantCache(TenantCacheFamily.Statistics)]
public class TempBasalController(
    ITempBasalRepository repo,
    IPatientDeviceStamper deviceStamper) : ControllerBase
{
    /// <summary>
    /// Lists temp basal spans, newest-first by default.
    /// Responses are never cached, so a just-started or just-cancelled span appears at once.
    /// </summary>
    /// <remarks>
    /// Never cached, per <see cref="Profiles.ProfileController.GetProfileSummary"/>: a just-started
    /// or just-cancelled span must not be invisible until a cached list body expires.
    /// </remarks>
    /// <param name="from">Start of the time range to list. Unbounded when omitted.</param>
    /// <param name="to">End of the time range to list. Unbounded when omitted.</param>
    /// <param name="limit">Maximum number of spans to return. Defaults to `100`.</param>
    /// <param name="offset">Number of spans to skip before the first one returned. Defaults to `0`.</param>
    /// <param name="sort">Order by span time: `timestamp_desc` or `timestamp_asc`. Defaults to `timestamp_desc`.</param>
    /// <param name="device">Return only spans from this device.</param>
    /// <param name="source">Return only spans from this data source.</param>
    /// <param name="includeTotal">Count every record in the range and return it as `pagination.total`. Defaults to `false`.</param>
    /// <param name="ct">Cancels the operation.</param>
    [HttpGet]
    [RequireScope(Scope.TreatmentsRead)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(PaginatedResponse<TempBasal>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<TempBasal>>> GetAll(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        [FromQuery] bool includeTotal = false,
        CancellationToken ct = default)
    {
        if (this.PrepareListPage(sort, ref limit, ref offset, out var descending) is { } invalid)
            return invalid;

        var data = (await repo.GetAsync(from, to, device, source, limit, offset, descending, ct)).ToList();
        var pagination = await PaginationInfo.ForPageAsync(limit, offset, data.Count, includeTotal, () => repo.CountAsync(from, to, ct));
        return Ok(new PaginatedResponse<TempBasal> { Data = data, Pagination = pagination });
    }

    /// <summary>
    /// Returns a single temp basal span by ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    [RequireScope(Scope.TreatmentsRead)]
    [ProducesResponseType(typeof(TempBasal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TempBasal>> GetById(Guid id, CancellationToken ct = default)
    {
        var result = await repo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Write temp basal spans and cancels in bulk (max 1000).
    /// Items are processed in timestamp order, so a batch may start a temp basal and cancel it later in the same request. Processing is per item, not all-or-nothing.
    /// A regular item with both `dataSource` and `syncIdentifier` updates the existing row matching that pair; any other item is inserted. A regular item whose pair belongs to a span the owner deleted is skipped and left out of the response, even in a one-item array, which returns `201 Created` with an empty array.
    /// A cancel (`isCancel: true`) ends the temp basal active at its timestamp at that instant; with none active it does nothing.
    /// A validation failure rejects the whole request with `400 Bad Request` and stores nothing. The response lists every record written or truncated, in processing order.
    /// </summary>
    /// <remarks>
    /// Items are processed in timestamp order, one at a time, so a batch may start a temp basal
    /// and cancel it later in the same request. Semantics are per-item, not all-or-nothing:
    ///
    /// - A regular item carrying both `dataSource` and `syncIdentifier` updates the row already
    ///   matched by that pair; all others insert.
    /// - A regular item whose pair is held by a span the owner deleted is skipped: it is not
    ///   re-created and is absent from the response, and the items around it are still written.
    ///   This holds for a one-item array too, which answers `201 Created` with an empty array
    ///   rather than `409 Conflict`, matching `POST bulk` on the other v4 record types: an array
    ///   endpoint reports a refused item by leaving it out, so a resending uploader is never
    ///   stuck retrying it. Restore the span instead to bring it back.
    /// - A cancel (`isCancel: true`) truncates the temp basal active at its timestamp by setting
    ///   the span end to that instant; with no active temp basal it is a no-op.
    ///
    /// Validation failures reject the whole request with `400 Bad Request` before anything is
    /// persisted. The response contains every record written or truncated, in processing order.
    /// </remarks>
    [HttpPost]
    [RequireScope(Scope.TreatmentsReadWrite)]
    [ProducesResponseType(typeof(TempBasal[]), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TempBasal[]>> CreateTempBasals(
        [FromBody] CreateTempBasalRequest[] requests,
        CancellationToken ct = default)
    {
        if (await this.ValidateBulkAsync(requests, "Temp basal", "temp basal", "temp basals", ct) is { } invalid)
            return invalid;

        if (requests.Any(r => !r.IsCancel && (r.Rate < 0 || r.DurationMinutes < 0)))
            return Problem(detail: "Rate and duration must not be negative", statusCode: 400, title: "Bad Request");

        var results = new List<TempBasal>(requests.Length);
        foreach (var request in requests.OrderBy(r => r.Timestamp))
        {
            if (request.IsCancel)
            {
                var truncated = await CancelActiveAtAsync(request.Timestamp.UtcDateTime, ct);
                if (truncated is not null)
                    results.Add(truncated);
                continue;
            }

            var model = MapToModel(request);
            // Attribute like the sensor-glucose native path — otherwise direct API records stay
            // unstamped and only ever surface as pseudo-devices.
            await deviceStamper.StampAsync([model], DeviceAttributionCategories.TempBasal, model.DataSource, ct);
            try
            {
                results.Add(await repo.CreateAsync(model, WriteOrigin.Live, ct));
            }
            catch (RecreationBlockedException)
            {
                // Skipped, per the remarks on this action.
            }
        }

        return StatusCode(201, results.ToArray());
    }

    /// <summary>
    /// Truncates the temp basal active at <paramref name="at"/>, or returns null when none is
    /// active (a cancel with nothing running is a no-op, matching pump behaviour).
    /// </summary>
    private async Task<TempBasal?> CancelActiveAtAsync(DateTime at, CancellationToken ct)
    {
        var active = await repo.GetActiveAtAsync(at, ct);
        if (active is null)
            return null;

        active.EndTimestamp = at;
        return await repo.UpdateAsync(active.Id, active, WriteOrigin.Live, ct);
    }

    private static TempBasal MapToModel(CreateTempBasalRequest request) => new()
    {
        StartTimestamp = request.Timestamp.UtcDateTime,
        EndTimestamp = request.DurationMinutes is { } duration
            ? request.Timestamp.UtcDateTime.AddMinutes(duration)
            : null,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        SyncIdentifier = request.SyncIdentifier,
        CorrelationId = request.CorrelationId,
        Rate = request.Rate,
        ScheduledRate = request.ScheduledRate,
        Origin = request.Origin ?? TempBasalOriginProvenance.UploaderDefault(request.App, app: null),
        OriginEvidence = request.Origin is null ? TempBasalOriginEvidence.Uploader : TempBasalOriginEvidence.Reported,
        PumpRecordId = request.PumpRecordId,
        ApsSnapshotId = request.ApsSnapshotId,
        AdditionalProperties = request.Origin is { } origin
            ? TempBasalAutomaticFlag.Keep(null, TempBasalAutomaticFlag.Of(origin))
            : null,
    };
}
