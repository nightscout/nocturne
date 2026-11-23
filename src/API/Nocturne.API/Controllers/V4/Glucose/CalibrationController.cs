using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Glucose;

/// <summary>
/// Continuous glucose monitor (CGM) calibrations: the slope, intercept, and scale values that a
/// sensor transmitter applies to the raw signal. List, read, create (one or in bulk), update and
/// delete calibrations. List deleted calibrations and restore them.
/// </summary>
/// <remarks>
/// Backed by <see cref="ICalibrationRepository"/>. Inherits standard list, get-by-ID, create, update, and delete operations from
/// <see cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>.
/// </remarks>
/// <seealso cref="ICalibrationRepository"/>
/// <seealso cref="Calibration"/>
/// <seealso cref="UpsertCalibrationRequest"/>
/// <seealso cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>
[ApiController]
[System.ComponentModel.DisplayName("Calibrations")]
[Tags("Glucose")]
[Route("api/v4/glucose/calibrations")]
[RequireScope(Scope.GlucoseRead)]
[Produces("application/json")]
public class CalibrationController(ICalibrationRepository repo)
    : V4CrudControllerBase<Calibration, UpsertCalibrationRequest, UpsertCalibrationRequest, ICalibrationRepository>(repo)
{
    /// <inheritdoc/>
    /// <remarks>Calibrations are glucose data; the legacy equivalent is a v1 <c>cal</c> entry.</remarks>
    public override string WriteScope => Scope.GlucoseReadWrite;

    /// <inheritdoc/>
    protected override V4BulkNaming BulkNaming => new("Calibration", "calibrations");

    /// <inheritdoc/>
    /// <remarks>
    /// Never cached, per <see cref="Profiles.ProfileController.GetProfileSummary"/>: a calibration the
    /// patient just entered must not be invisible until a cached list body expires.
    /// </remarks>
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public override Task<ActionResult<PaginatedResponse<Calibration>>> GetAll(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        [FromQuery] bool includeTotal = false,
        CancellationToken ct = default)
        => base.GetAll(from, to, limit, offset, sort, device, source, includeTotal, ct);

    /// <summary>
    /// Maps a <see cref="UpsertCalibrationRequest"/> to a new <see cref="Calibration"/> domain model for creation.
    /// </summary>
    /// <param name="request">The create request containing slope, intercept, scale, and device metadata.</param>
    /// <returns>A new <see cref="Calibration"/> instance ready for persistence.</returns>
    protected override Calibration MapCreateToModel(UpsertCalibrationRequest request) => new Calibration
    {
        Slope = request.Slope,
        Intercept = request.Intercept,
        Scale = request.Scale,
    }.WithHeaderFrom(request);

    /// <summary>
    /// Maps a <see cref="UpsertCalibrationRequest"/> to an updated <see cref="Calibration"/>, preserving
    /// immutable fields (<c>CorrelationId</c>, <c>LegacyId</c>, <c>CreatedAt</c>, and <c>AdditionalProperties</c>)
    /// from the <paramref name="existing"/> record.
    /// </summary>
    /// <param name="id">The record ID being updated.</param>
    /// <param name="request">The update request.</param>
    /// <param name="existing">The existing record whose immutable fields are carried forward.</param>
    /// <returns>A <see cref="Calibration"/> instance with updated mutable fields and preserved immutable fields.</returns>
    protected override Calibration MapUpdateToModel(Guid id, UpsertCalibrationRequest request, Calibration existing) => new Calibration
    {
        Id = id,
        Slope = request.Slope,
        Intercept = request.Intercept,
        Scale = request.Scale,
        CorrelationId = existing.CorrelationId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
        AdditionalProperties = existing.AdditionalProperties,
    }.WithHeaderFrom(request);
}
