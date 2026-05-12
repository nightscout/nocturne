using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Glucose;

/// <summary>
/// Controller for managing blood glucose meter readings. Meter readings are discrete fingerstick
/// values expressed in mg/dL and recorded by the uploader or directly by the patient device.
/// Provides full CRUD operations backed by <see cref="IMeterGlucoseRepository"/>.
/// </summary>
/// <remarks>
/// Inherits standard list, get-by-ID, create, update, and delete operations from
/// <see cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>.
/// The <c>GetAll</c> response is cached for 120 seconds with vary-by-query-keys.
/// </remarks>
/// <seealso cref="IMeterGlucoseRepository"/>
/// <seealso cref="MeterGlucose"/>
/// <seealso cref="UpsertMeterGlucoseRequest"/>
/// <seealso cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>
[ApiController]
[Tags("Glucose")]
[Route("api/v4/glucose/meter")]
[Authorize]
[Produces("application/json")]
public class MeterGlucoseController(IMeterGlucoseRepository repo)
    : V4CrudControllerBase<MeterGlucose, UpsertMeterGlucoseRequest, UpsertMeterGlucoseRequest, IMeterGlucoseRepository>(repo)
{
    /// <inheritdoc/>
    /// <remarks>Response is cached for 120 seconds, varied by all query parameters.</remarks>
    [ResponseCache(Duration = 120, VaryByQueryKeys = new[] { "*" })]
    public override Task<ActionResult<PaginatedResponse<MeterGlucose>>> GetAll(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
        => base.GetAll(from, to, limit, offset, sort, device, source, ct);

    /// <summary>
    /// Maps a <see cref="UpsertMeterGlucoseRequest"/> to a new <see cref="MeterGlucose"/> domain model for creation.
    /// </summary>
    /// <param name="request">The create request containing the mg/dL value and device metadata.</param>
    /// <returns>A new <see cref="MeterGlucose"/> instance ready for persistence.</returns>
    protected override MeterGlucose MapCreateToModel(UpsertMeterGlucoseRequest request) => new()
    {
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        Mgdl = request.Mgdl,
    };

    /// <summary>
    /// Maps a <see cref="UpsertMeterGlucoseRequest"/> to an updated <see cref="MeterGlucose"/>, preserving
    /// immutable fields (<c>CorrelationId</c>, <c>LegacyId</c>, <c>CreatedAt</c>, and <c>AdditionalProperties</c>)
    /// from the <paramref name="existing"/> record.
    /// </summary>
    /// <param name="id">The record ID being updated.</param>
    /// <param name="request">The update request.</param>
    /// <param name="existing">The existing record whose immutable fields are carried forward.</param>
    /// <returns>A <see cref="MeterGlucose"/> instance with updated mutable fields and preserved immutable fields.</returns>
    protected override MeterGlucose MapUpdateToModel(Guid id, UpsertMeterGlucoseRequest request, MeterGlucose existing) => new()
    {
        Id = id,
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        Mgdl = request.Mgdl,
        CorrelationId = existing.CorrelationId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
        AdditionalProperties = existing.AdditionalProperties,
    };
}
