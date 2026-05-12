using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Glucose;

/// <summary>
/// Controller for managing CGM calibration records — the slope, intercept, and scale values
/// that a sensor transmitter applies to raw signal to produce glucose readings.
/// Provides CRUD operations backed by <see cref="ICalibrationRepository"/>.
/// </summary>
/// <remarks>
/// Inherits standard list, get-by-ID, create, update, and delete operations from
/// <see cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>.
/// The <c>GetAll</c> response is cached for 120 seconds with vary-by-query-keys to minimise
/// database pressure when the dashboard polls for calibration data.
/// </remarks>
/// <seealso cref="ICalibrationRepository"/>
/// <seealso cref="Calibration"/>
/// <seealso cref="UpsertCalibrationRequest"/>
/// <seealso cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>
[ApiController]
[Tags("Glucose")]
[Route("api/v4/glucose/calibrations")]
[Authorize]
[Produces("application/json")]
public class CalibrationController(ICalibrationRepository repo)
    : V4CrudControllerBase<Calibration, UpsertCalibrationRequest, UpsertCalibrationRequest, ICalibrationRepository>(repo)
{
    /// <inheritdoc/>
    /// <remarks>Response is cached for 120 seconds, varied by all query parameters.</remarks>
    [ResponseCache(Duration = 120, VaryByQueryKeys = new[] { "*" })]
    public override Task<ActionResult<PaginatedResponse<Calibration>>> GetAll(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
        => base.GetAll(from, to, limit, offset, sort, device, source, ct);

    /// <summary>
    /// Maps a <see cref="UpsertCalibrationRequest"/> to a new <see cref="Calibration"/> domain model for creation.
    /// </summary>
    /// <param name="request">The create request containing slope, intercept, scale, and device metadata.</param>
    /// <returns>A new <see cref="Calibration"/> instance ready for persistence.</returns>
    protected override Calibration MapCreateToModel(UpsertCalibrationRequest request) => new()
    {
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        Slope = request.Slope,
        Intercept = request.Intercept,
        Scale = request.Scale,
    };

    /// <summary>
    /// Maps a <see cref="UpsertCalibrationRequest"/> to an updated <see cref="Calibration"/>, preserving
    /// immutable fields (<c>CorrelationId</c>, <c>LegacyId</c>, <c>CreatedAt</c>, and <c>AdditionalProperties</c>)
    /// from the <paramref name="existing"/> record.
    /// </summary>
    /// <param name="id">The record ID being updated.</param>
    /// <param name="request">The update request.</param>
    /// <param name="existing">The existing record whose immutable fields are carried forward.</param>
    /// <returns>A <see cref="Calibration"/> instance with updated mutable fields and preserved immutable fields.</returns>
    protected override Calibration MapUpdateToModel(Guid id, UpsertCalibrationRequest request, Calibration existing) => new()
    {
        Id = id,
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        Slope = request.Slope,
        Intercept = request.Intercept,
        Scale = request.Scale,
        CorrelationId = existing.CorrelationId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
        AdditionalProperties = existing.AdditionalProperties,
    };
}
