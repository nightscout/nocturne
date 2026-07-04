using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Controllers.V4.Glucose;

/// <summary>
/// Controller for managing CGM sensor glucose readings.
/// Provides CRUD operations and bulk creation for <see cref="SensorGlucose"/> records.
/// After creation, evaluates glucose alerts against the canonical stream via
/// <see cref="ICanonicalAlertEvaluator"/>.
/// </summary>
/// <seealso cref="ISensorGlucoseRepository"/>
/// <seealso cref="SensorGlucose"/>
/// <seealso cref="UpsertSensorGlucoseRequest"/>
/// <seealso cref="ICanonicalAlertEvaluator"/>
/// <seealso cref="V4CrudControllerBase{TModel, TCreateRequest, TUpdateRequest, TRepository}"/>
[ApiController]
[Tags("Glucose")]
[Route("api/v4/glucose/sensor")]
[Authorize]
[Produces("application/json")]
public class SensorGlucoseController(
    ISensorGlucoseRepository repo,
    IGlucoseProcessingResolver glucoseResolver,
    ICanonicalAlertEvaluator alertEvaluator,
    IPatientDeviceStamper deviceStamper,
    ILogger<SensorGlucoseController> logger)
    : V4CrudControllerBase<SensorGlucose, UpsertSensorGlucoseRequest, UpsertSensorGlucoseRequest, ISensorGlucoseRepository>(repo)
{
    /// <summary>
    /// Lists sensor glucose readings. Adds an optional <c>patientDeviceId</c> query filter on top of the base
    /// list surface: when set, results are that registered device's raw readings (canonical stream selection is
    /// bypassed); when unset, the caller sees every stored reading. Pagination totals match the base
    /// device/source behaviour (the count is unscoped by the device filters).
    /// </summary>
    /// <remarks>
    /// The <c>patientDeviceId</c> query parameter is read directly from the request because the base list
    /// signature (shared by every V4 read controller) has no device-attribution concept — binding it here keeps
    /// a single <c>GET</c> action while adding the sensor-glucose-only filter.
    /// </remarks>
    [ResponseCache(Duration = 90, VaryByQueryKeys = new[] { "*" })]
    public override async Task<ActionResult<PaginatedResponse<SensorGlucose>>> GetAll(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        Guid? patientDeviceId = null;
        if (Request.Query.TryGetValue("patientDeviceId", out var raw) && Guid.TryParse(raw, out var parsed))
            patientDeviceId = parsed;

        var descending = sort == "timestamp_desc";
        var data = await Repository.GetAsync(from, to, device, source, limit, offset, descending,
            nativeOnly: false, afterTimestamp: null, afterId: null, patientDeviceId: patientDeviceId, ct: ct);
        var total = await Repository.CountAsync(from, to, ct);
        return Ok(new PaginatedResponse<SensorGlucose> { Data = data, Pagination = new PaginationInfo(limit, offset, total) });
    }

    public override async Task<ActionResult<SensorGlucose>> Create([FromBody] UpsertSensorGlucoseRequest request, CancellationToken ct = default)
    {
        var model = MapCreateToModel(request);

        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");

        await glucoseResolver.ResolveAsync(model, request.GlucoseProcessing, request.SmoothedMgdl, request.UnsmoothedMgdl, ct);

        // V4 REST writes bypass the connector/decomposer ingest paths, so attribute here — otherwise
        // direct API records stay unstamped and only ever surface as pseudo-devices. Fills only when
        // the record is unattributed; the canonical stream still governs reads.
        await deviceStamper.StampAsync([model], [DeviceCategory.CGM], model.DataSource, ct);

        var created = await Repository.CreateAsync(model, WriteOrigin.Live, ct);
        created = await OnAfterCreateAsync(created, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    protected override SensorGlucose MapCreateToModel(UpsertSensorGlucoseRequest request) => new()
    {
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        Mgdl = request.Mgdl,
        Direction = request.Direction,
        TrendRate = request.TrendRate,
        Noise = request.Noise,
        Filtered = request.Filtered,
        Unfiltered = request.Unfiltered,
        Delta = request.Delta,
    };

    protected override SensorGlucose MapUpdateToModel(Guid id, UpsertSensorGlucoseRequest request, SensorGlucose existing) => new()
    {
        Id = id,
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        Mgdl = request.Mgdl,
        Direction = request.Direction,
        TrendRate = request.TrendRate,
        Noise = request.Noise,
        Filtered = request.Filtered,
        Unfiltered = request.Unfiltered,
        Delta = request.Delta,
        CorrelationId = existing.CorrelationId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
        AdditionalProperties = existing.AdditionalProperties,
        // Preserve attribution across edits; the update DTO carries no device identity, so rebuilding
        // the model without this would silently drop the record to a pseudo-device.
        PatientDeviceId = existing.PatientDeviceId,
    };

    public override async Task<ActionResult<SensorGlucose>> Update(Guid id, [FromBody] UpsertSensorGlucoseRequest request, CancellationToken ct = default)
    {
        var existing = await Repository.GetByIdAsync(id, ct);
        if (existing is null)
            return NotFound();

        var model = MapUpdateToModel(id, request, existing);

        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");

        await glucoseResolver.ResolveAsync(model, request.GlucoseProcessing, request.SmoothedMgdl, request.UnsmoothedMgdl, ct);

        // No-op when attribution was preserved above; re-attributes only records still unstamped.
        await deviceStamper.StampAsync([model], [DeviceCategory.CGM], model.DataSource, ct);

        try
        {
            var updated = await Repository.UpdateAsync(id, model, WriteOrigin.Live, ct);

            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Create multiple sensor glucose readings in bulk (max 1000).
    /// </summary>
    [HttpPost("bulk")]
    [ProducesResponseType(typeof(SensorGlucose[]), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SensorGlucose[]>> CreateSensorGlucoseBulk(
        [FromBody] UpsertSensorGlucoseRequest[] requests,
        CancellationToken ct = default)
    {
        if (requests is not { Length: > 0 })
            return Problem(detail: "Sensor glucose data is required", statusCode: 400, title: "Bad Request");

        if (requests.Length > 1000)
            return Problem(detail: "Bulk operations are limited to 1000 readings per request", statusCode: 400, title: "Bad Request");

        var models = requests.Select(MapCreateToModel).ToList();

        for (var i = 0; i < models.Count; i++)
            await glucoseResolver.ResolveAsync(models[i], requests[i].GlucoseProcessing, requests[i].SmoothedMgdl, requests[i].UnsmoothedMgdl, ct);

        // Attribute the batch before persisting (see Create). Per-record DataSource drives matching,
        // so no batch-level source is needed for a mixed-source bulk upload.
        await deviceStamper.StampAsync(models, [DeviceCategory.CGM], batchSource: null, ct);

        var created = await Repository.BulkCreateAsync(models, WriteOrigin.Live, ct);
        var createdArray = created.ToArray();

        // Alarms evaluate against the canonical stream, not the just-created batch.
        if (createdArray.Any(r => r.Mgdl > 0))
            await alertEvaluator.EvaluateAsync(ct);

        return StatusCode(201, createdArray);
    }

    protected override async Task<SensorGlucose> OnAfterCreateAsync(SensorGlucose created, CancellationToken ct)
    {
        if (created.Mgdl > 0)
            await alertEvaluator.EvaluateAsync(ct);

        return created;
    }
}
