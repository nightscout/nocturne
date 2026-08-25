using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Controllers.V4.Treatments;

/// <summary>
/// Controller for managing insulin bolus records.
/// Exposes standard V4 CRUD operations via <see cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>.
/// </summary>
/// <remarks>
/// On update, immutable fields (<see cref="Bolus.BolusType"/>, <see cref="Bolus.Kind"/>,
/// <see cref="Bolus.LegacyId"/>, <see cref="Bolus.CreatedAt"/>, <see cref="Bolus.PumpRecordId"/>,
/// <see cref="Bolus.DeviceId"/>, and <see cref="Bolus.AdditionalProperties"/>) are preserved from the
/// existing record. <see cref="Bolus.CorrelationId"/> falls back to the existing value if the request
/// does not supply one.
/// </remarks>
/// <seealso cref="IBolusRepository"/>
/// <seealso cref="Bolus"/>
/// <seealso cref="CreateBolusRequest"/>
/// <seealso cref="UpdateBolusRequest"/>
/// <seealso cref="PatientDeviceAttribution"/>
[ApiController]
[Tags("Treatments")]
[Route("api/v4/insulin/boluses")]
[RequireScope(Scope.TreatmentsRead)]
[Produces("application/json")]
public class BolusController(
    IBolusRepository repo,
    IPatientInsulinRepository insulinRepo,
    IPatientDeviceRepository patientDevices,
    IPatientDeviceStamper deviceStamper)
    : V4CrudControllerBase<Bolus, CreateBolusRequest, UpdateBolusRequest, IBolusRepository>(repo)
{
    /// <inheritdoc/>
    /// <remarks>Boluses are treatments; the legacy equivalent is a v1 insulin treatment.</remarks>
    public override string WriteScope => Scope.TreatmentsReadWrite;

    /// <inheritdoc/>
    /// <remarks>
    /// Never cached, per <see cref="Profiles.ProfileController.GetProfileSummary"/>: a just-entered
    /// bolus must not be invisible until a cached list body expires.
    /// </remarks>
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public override Task<ActionResult<PaginatedResponse<Bolus>>> GetAll(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
        => base.GetAll(from, to, limit, offset, sort, device, source, ct);

    /// <inheritdoc/>
    public override async Task<ActionResult<Bolus>> Create([FromBody] CreateBolusRequest request, CancellationToken ct = default)
    {
        var model = MapCreateToModel(request);

        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");

        await EnrichInsulinContextAsync(model, request.PatientInsulinId, ct);

        // V4 REST writes bypass the connector/decomposer ingest paths, so attribute here — otherwise
        // direct API records stay unstamped and only ever surface as pseudo-devices.
        if (await ApplyAttributionAsync(model, request.PatientDeviceId, existing: null, ct) is { } error)
            return error;

        var created = await Repository.CreateAsync(model, WriteOrigin.Live, ct);
        created = await OnAfterCreateAsync(created, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <inheritdoc/>
    public override async Task<ActionResult<Bolus>> Update(Guid id, [FromBody] UpdateBolusRequest request, CancellationToken ct = default)
    {
        var existing = await Repository.GetByIdAsync(id, ct);
        if (existing is null)
            return NotFound();

        var model = MapUpdateToModel(id, request, existing);

        if (model.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");

        await EnrichInsulinContextAsync(model, request.PatientInsulinId, ct);

        if (await ApplyAttributionAsync(model, request.PatientDeviceId, existing.PatientDeviceId, ct) is { } error)
            return error;

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

    /// <summary>Maps a <see cref="CreateBolusRequest"/> to a new <see cref="Bolus"/> domain model.</summary>
    /// <param name="request">The inbound create request.</param>
    /// <returns>A new <see cref="Bolus"/> with all fields populated from the request. <see cref="Bolus.CorrelationId"/> defaults to a new UUID v7 when not supplied.</returns>
    protected override Bolus MapCreateToModel(CreateBolusRequest request) => new()
    {
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        Insulin = request.Insulin,
        Programmed = request.Programmed,
        Delivered = request.Delivered,
        BolusType = request.BolusType,
        Kind = request.Kind,
        Automatic = request.Automatic,
        Duration = request.Duration,
        SyncIdentifier = request.SyncIdentifier,
        InsulinType = request.InsulinType,
        Unabsorbed = request.Unabsorbed,
        BolusCalculationId = request.BolusCalculationId,
        ApsSnapshotId = request.ApsSnapshotId,
        CorrelationId = request.CorrelationId ?? Guid.CreateVersion7(),
    };

    /// <summary>Maps an <see cref="UpdateBolusRequest"/> onto a <see cref="Bolus"/> domain model, preserving immutable fields from the existing record.</summary>
    /// <param name="id">The bolus ID to carry forward.</param>
    /// <param name="request">The inbound update request.</param>
    /// <param name="existing">The existing <see cref="Bolus"/> record; immutable fields (<c>BolusType</c>, <c>Kind</c>, <c>LegacyId</c>, <c>CreatedAt</c>, <c>PumpRecordId</c>, <c>DeviceId</c>, <c>AdditionalProperties</c>) are copied from here.</param>
    /// <returns>A fully-populated <see cref="Bolus"/> ready for persistence.</returns>
    protected override Bolus MapUpdateToModel(Guid id, UpdateBolusRequest request, Bolus existing) => new()
    {
        Id = id,
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        Insulin = request.Insulin,
        Programmed = request.Programmed,
        Delivered = request.Delivered,
        BolusType = existing.BolusType,
        Kind = existing.Kind,
        Automatic = request.Automatic,
        Duration = request.Duration,
        SyncIdentifier = request.SyncIdentifier,
        InsulinType = request.InsulinType,
        Unabsorbed = request.Unabsorbed,
        BolusCalculationId = request.BolusCalculationId,
        ApsSnapshotId = request.ApsSnapshotId,
        CorrelationId = request.CorrelationId ?? existing.CorrelationId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
        PumpRecordId = existing.PumpRecordId,
        DeviceId = existing.DeviceId,
        AdditionalProperties = existing.AdditionalProperties,
    };

    /// <summary>
    /// Create or update boluses in bulk (max 1000).
    /// </summary>
    /// <remarks>
    /// Array semantics are per-item upsert, not all-or-nothing: each bolus carrying both
    /// `dataSource` and `syncIdentifier` updates the row already matched by that pair; all others
    /// insert. Validation failures reject the whole request with `400 Bad Request` before anything
    /// is persisted.
    /// </remarks>
    [HttpPost("bulk")]
    [RequireDeclaredWriteScope]
    [ProducesResponseType(typeof(Bolus[]), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Bolus[]>> CreateBolusesBulk(
        [FromBody] CreateBolusRequest[] requests,
        CancellationToken ct = default)
    {
        if (requests is not { Length: > 0 })
            return Problem(detail: "Bolus data is required", statusCode: 400, title: "Bad Request");

        if (requests.Length > 1000)
            return Problem(detail: "Bulk operations are limited to 1000 boluses per request", statusCode: 400, title: "Bad Request");

        if (requests.Any(r => r.Timestamp == default))
            return Problem(detail: "Timestamp must be set on every bolus", statusCode: 400, title: "Bad Request");

        if (requests.Any(r => !string.IsNullOrEmpty(r.SyncIdentifier) && string.IsNullOrEmpty(r.DataSource)))
            return Problem(detail: "DataSource is required when SyncIdentifier is supplied", statusCode: 400, title: "Bad Request");

        var models = new List<Bolus>(requests.Length);
        foreach (var request in requests)
        {
            var model = MapCreateToModel(request);
            await EnrichInsulinContextAsync(model, request.PatientInsulinId, ct);
            models.Add(model);
        }

        // Attribute the batch before persisting (see Create). Per-record DataSource drives matching,
        // so no batch-level source is needed for a mixed-source bulk upload.
        var attributionError = await PatientDeviceAttribution.ApplyManyAsync(
            [.. models.Select((m, i) => ((IDeviceAttributed)m, requests[i].PatientDeviceId))],
            patientDevices, deviceStamper, DeviceAttributionCategories.Bolus, batchSource: null, ct);
        if (attributionError is not null)
            return Problem(detail: attributionError, statusCode: 400, title: "Bad Request");

        var persisted = await Repository.BulkCreateAsync(models, WriteOrigin.Live, ct);
        return StatusCode(201, persisted.ToArray());
    }

    /// <summary>
    /// Delete a bolus by its external sync identifier (dataSource + syncIdentifier pair).
    /// </summary>
    [HttpDelete("by-sync-id")]
    [RequireDeclaredWriteScope]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> DeleteBySyncIdentifier(
        [FromQuery] string dataSource,
        [FromQuery] string syncIdentifier,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(dataSource) || string.IsNullOrEmpty(syncIdentifier))
            return BadRequest("dataSource and syncIdentifier are required");

        var deleted = await ((IBolusRepository)Repository).DeleteBySyncIdentifierAsync(dataSource, syncIdentifier, WriteOrigin.Live, ct);
        return deleted > 0 ? NoContent() : NotFound();
    }

    /// <summary>
    /// Settles the bolus's device attribution from the request. Returns a 400 result when an explicit
    /// id doesn't resolve (tenant scoping makes a cross-tenant id indistinguishable from a nonexistent
    /// one), or <c>null</c> on success.
    /// </summary>
    private async Task<ObjectResult?> ApplyAttributionAsync(Bolus model, Guid? requested, Guid? existing, CancellationToken ct)
    {
        var error = await PatientDeviceAttribution.ApplyAsync(
            model, requested, existing, patientDevices, deviceStamper,
            DeviceAttributionCategories.Bolus, ct);

        return error is null ? null : Problem(detail: error, statusCode: 400, title: "Bad Request");
    }

    private async Task EnrichInsulinContextAsync(Bolus model, Guid? patientInsulinId, CancellationToken ct)
    {
        if (patientInsulinId is null)
            return;

        var insulin = await insulinRepo.GetByIdAsync(patientInsulinId.Value, ct);
        if (insulin is null)
            return;

        model.InsulinContext = new TreatmentInsulinContext
        {
            PatientInsulinId = insulin.Id,
            InsulinName = insulin.Name,
            Dia = insulin.Dia,
            Peak = insulin.Peak,
            Curve = insulin.Curve,
            Concentration = insulin.Concentration,
        };
        model.InsulinType = insulin.Name;
    }
}
