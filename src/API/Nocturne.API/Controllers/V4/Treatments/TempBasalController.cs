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
public class TempBasalController(
    ITempBasalRepository repo,
    IPatientDeviceStamper deviceStamper) : ControllerBase
{
    /// <summary>
    /// Lists temp basal spans, newest-first by default.
    /// </summary>
    /// <remarks>
    /// Never cached, per <see cref="Profiles.ProfileController.GetProfileSummary"/>: a just-started
    /// or just-cancelled span must not be invisible until a cached list body expires.
    /// </remarks>
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
        CancellationToken ct = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        limit = V4ReadLimits.ClampLimit(limit);
        offset = V4ReadLimits.ClampOffset(offset);

        var descending = sort == "timestamp_desc";
        var data = await repo.GetAsync(from, to, device, source, limit, offset, descending, ct);
        var total = await repo.CountAsync(from, to, ct);
        return Ok(new PaginatedResponse<TempBasal> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
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
    /// </summary>
    /// <remarks>
    /// Items are processed in timestamp order, one at a time, so a batch may start a temp basal
    /// and cancel it later in the same request. Semantics are per-item, not all-or-nothing:
    ///
    /// - A regular item carrying both `dataSource` and `syncIdentifier` updates the row already
    ///   matched by that pair; all others insert.
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
        if (this.ValidateBulk(requests, "Temp basal", "temp basal", "temp basals") is { } invalid)
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
            results.Add(await repo.CreateAsync(model, WriteOrigin.Live, ct));
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
        Origin = request.Origin ?? TempBasalOrigin.Manual,
        PumpRecordId = request.PumpRecordId,
        ApsSnapshotId = request.ApsSnapshotId,
    };
}
