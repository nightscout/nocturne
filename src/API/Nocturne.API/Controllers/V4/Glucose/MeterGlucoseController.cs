using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
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
/// </remarks>
/// <seealso cref="IMeterGlucoseRepository"/>
/// <seealso cref="MeterGlucose"/>
/// <seealso cref="UpsertMeterGlucoseRequest"/>
/// <seealso cref="PatientDeviceAttribution"/>
/// <seealso cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>
[ApiController]
[Tags("Glucose")]
[Route("api/v4/glucose/meter")]
[RequireScope(Scope.GlucoseRead)]
[Produces("application/json")]
public class MeterGlucoseController(
    IMeterGlucoseRepository repo,
    IPatientDeviceRepository patientDevices,
    IPatientDeviceStamper deviceStamper)
    : V4CrudControllerBase<MeterGlucose, UpsertMeterGlucoseRequest, UpsertMeterGlucoseRequest, IMeterGlucoseRepository>(repo)
{
    /// <inheritdoc/>
    /// <remarks>Meter readings are glucose data; the legacy equivalent is a v1 <c>mbg</c> entry.</remarks>
    public override string WriteScope => Scope.GlucoseReadWrite;

    /// <inheritdoc/>
    protected override V4BulkNaming BulkNaming => new("Meter glucose", "reading", "readings");

    /// <inheritdoc/>
    /// <remarks>
    /// Never cached, per <see cref="Profiles.ProfileController.GetProfileSummary"/>: a fingerstick the
    /// patient just entered must not be invisible until a cached list body expires.
    /// </remarks>
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public override Task<ActionResult<PaginatedResponse<MeterGlucose>>> GetAll(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
        => base.GetAll(from, to, limit, offset, sort, device, source, ct);

    /// <inheritdoc/>
    /// <remarks>
    /// V4 REST writes bypass the connector/decomposer ingest paths, so attribution happens here —
    /// otherwise direct API records stay unstamped and only ever surface as pseudo-devices.
    /// </remarks>
    protected override Task<ObjectResult?> OnBeforeCreateAsync(
        MeterGlucose model, UpsertMeterGlucoseRequest request, CancellationToken ct)
        => ApplyAttributionAsync(model, request, existing: null, ct);

    /// <inheritdoc/>
    protected override Task<ObjectResult?> OnBeforeUpdateAsync(
        MeterGlucose model, UpsertMeterGlucoseRequest request, MeterGlucose existing, CancellationToken ct)
        => ApplyAttributionAsync(model, request, existing.PatientDeviceId, ct);

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
    /// immutable fields (<c>CorrelationId</c>, <c>LegacyId</c>, <c>CreatedAt</c>, and
    /// <c>AdditionalProperties</c>) from the <paramref name="existing"/> record.
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

    /// <inheritdoc/>
    /// <remarks>
    /// The batch form rather than a loop over <see cref="OnBeforeCreateAsync"/>: one stamper pass
    /// resolves the whole payload, and per-record DataSource drives matching, so no batch-level
    /// source is needed for a mixed-source upload.
    /// </remarks>
    protected override async Task<ObjectResult?> OnBeforeBulkCreateAsync(
        IReadOnlyList<MeterGlucose> models, IReadOnlyList<UpsertMeterGlucoseRequest> requests, CancellationToken ct)
    {
        var error = await PatientDeviceAttribution.ApplyManyAsync(
            [.. models.Select((m, i) => ((IDeviceAttributed)m, requests[i].PatientDeviceId))],
            patientDevices, deviceStamper, DeviceAttributionCategories.MeterGlucose, batchSource: null, ct);

        return error is null ? null : Problem(detail: error, statusCode: 400, title: "Bad Request");
    }

    /// <summary>
    /// Settles the reading's device attribution from the request. Returns a 400 result when an explicit
    /// id doesn't resolve (tenant scoping makes a cross-tenant id indistinguishable from a nonexistent
    /// one), or <c>null</c> on success.
    /// </summary>
    private async Task<ObjectResult?> ApplyAttributionAsync(MeterGlucose model, UpsertMeterGlucoseRequest request, Guid? existing, CancellationToken ct)
    {
        var error = await PatientDeviceAttribution.ApplyAsync(
            model, request.PatientDeviceId, existing, patientDevices, deviceStamper,
            DeviceAttributionCategories.MeterGlucose, ct);

        return error is null ? null : Problem(detail: error, statusCode: 400, title: "Bad Request");
    }
}
