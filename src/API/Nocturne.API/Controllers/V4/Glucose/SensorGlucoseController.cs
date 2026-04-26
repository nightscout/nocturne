using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Glucose;

/// <summary>
/// Controller for managing CGM sensor glucose readings.
/// Provides CRUD operations and bulk creation for <see cref="SensorGlucose"/> records.
/// After creation, evaluates glucose alerts via <see cref="IAlertOrchestrator"/>.
/// </summary>
/// <seealso cref="ISensorGlucoseRepository"/>
/// <seealso cref="SensorGlucose"/>
/// <seealso cref="UpsertSensorGlucoseRequest"/>
/// <seealso cref="IAlertOrchestrator"/>
/// <seealso cref="V4CrudControllerBase{TModel, TCreateRequest, TUpdateRequest, TRepository}"/>
[ApiController]
[Route("api/v4/glucose/sensor")]
[Authorize]
[Produces("application/json")]
public class SensorGlucoseController(
    ISensorGlucoseRepository repo,
    IGlucoseProcessingResolver glucoseResolver,
    IAlertOrchestrator alertOrchestrator,
    ILogger<SensorGlucoseController> logger)
    : V4CrudControllerBase<SensorGlucose, UpsertSensorGlucoseRequest, UpsertSensorGlucoseRequest, ISensorGlucoseRepository>(repo)
{
    [ResponseCache(Duration = 90, VaryByQueryKeys = new[] { "*" })]
    public override Task<ActionResult<PaginatedResponse<SensorGlucose>>> GetAll(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
        => base.GetAll(from, to, limit, offset, sort, device, source, ct);

    public override async Task<ActionResult<SensorGlucose>> Create([FromBody] UpsertSensorGlucoseRequest request, CancellationToken ct = default)
    {
        var model = MapCreateToModel(request);

        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");

        await glucoseResolver.ResolveAsync(model, request.GlucoseProcessing, request.SmoothedMgdl, request.UnsmoothedMgdl, ct);

        var created = await Repository.CreateAsync(model, ct);
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

        try
        {
            var updated = await Repository.UpdateAsync(id, model, ct);
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

        var created = await Repository.BulkCreateAsync(models, ct);
        var createdArray = created.ToArray();

        // Evaluate alerts for the most recent reading only (not every historical reading during backfill)
        var mostRecent = createdArray.OrderByDescending(r => r.Timestamp).FirstOrDefault();
        if (mostRecent is { Mgdl: > 0 })
        {
            try
            {
                var context = new SensorContext
                {
                    LatestValue = (decimal)mostRecent.Mgdl,
                    LatestTimestamp = mostRecent.Timestamp,
                    TrendRate = (decimal?)mostRecent.TrendRate,
                    LastReadingAt = mostRecent.Timestamp,
                };
                await alertOrchestrator.EvaluateAsync(context, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Alert evaluation failed after bulk SensorGlucose creation");
            }
        }

        return StatusCode(201, createdArray);
    }

    protected override async Task<SensorGlucose> OnAfterCreateAsync(SensorGlucose created, CancellationToken ct)
    {
        try
        {
            if (created.Mgdl > 0)
            {
                var context = new SensorContext
                {
                    LatestValue = (decimal)created.Mgdl,
                    LatestTimestamp = created.Timestamp,
                    TrendRate = (decimal?)created.TrendRate,
                    LastReadingAt = created.Timestamp,
                };

                await alertOrchestrator.EvaluateAsync(context, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Alert evaluation failed after V4 SensorGlucose creation");
        }

        return created;
    }
}
