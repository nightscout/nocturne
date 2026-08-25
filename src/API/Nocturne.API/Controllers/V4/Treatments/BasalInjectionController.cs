using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Controllers.V4.Treatments;

/// <summary>
/// CRUD for long-acting basal insulin injections (MDI).
/// Exposes standard V4 CRUD operations via <see cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>,
/// with additional validation and idempotent upsert on (<see cref="BasalInjection.DataSource"/>, <see cref="BasalInjection.SyncIdentifier"/>).
/// </summary>
/// <remarks>
/// Both create and update enforce the same rules: <see cref="BasalInjection.Units"/> must be in (0, 500],
/// <see cref="BasalInjection.Timestamp"/> may not be more than five minutes in the future, and — when the
/// request carries a <c>PatientInsulinId</c> — the referenced <see cref="PatientInsulin"/> must exist with
/// role <see cref="InsulinRole.Basal"/> or <see cref="InsulinRole.Both"/> and be active at the injection
/// time. The server resolves <see cref="PatientInsulin"/> fresh on every write to populate the
/// <see cref="TreatmentInsulinContext"/> snapshot.
///
/// The insulin reference is optional, matching <see cref="BolusController"/>: uploader-style clients that
/// know nothing about the patient's insulin catalog omit it, and the record is stored with a <c>null</c>
/// <see cref="BasalInjection.InsulinContext"/>.
///
/// On update, immutable fields (<see cref="BasalInjection.LegacyId"/>, <see cref="BasalInjection.CreatedAt"/>)
/// are preserved from the existing record. <see cref="BasalInjection.CorrelationId"/> falls back to the
/// existing value if the request does not supply one.
/// </remarks>
/// <seealso cref="IBasalInjectionRepository"/>
/// <seealso cref="BasalInjection"/>
/// <seealso cref="CreateBasalInjectionRequest"/>
/// <seealso cref="UpdateBasalInjectionRequest"/>
[ApiController]
[Route("api/v4/insulin/basal-injections")]
[RequireScope(Scope.TreatmentsRead)]
[Produces("application/json")]
public class BasalInjectionController(
    IBasalInjectionRepository repo,
    IPatientInsulinRepository insulinRepo)
    : V4CrudControllerBase<BasalInjection, CreateBasalInjectionRequest, UpdateBasalInjectionRequest, IBasalInjectionRepository>(repo)
{
    private const double UnitsHardCeiling = 500.0;
    private const int FutureToleranceMinutes = 5;

    /// <inheritdoc/>
    /// <remarks>Basal injections are treatments; the legacy equivalent is a v1 insulin treatment.</remarks>
    public override string WriteScope => Scope.TreatmentsReadWrite;

    /// <inheritdoc/>
    public override async Task<ActionResult<BasalInjection>> Create(
        [FromBody] CreateBasalInjectionRequest request, CancellationToken ct = default)
    {
        if (ValidateUnitsAndTimestamp(request.Units, request.Timestamp) is { } unitsOrTsProblem)
            return unitsOrTsProblem;

        // Idempotent upsert: if a record with this (DataSource, SyncIdentifier) already exists, return it.
        if (!string.IsNullOrEmpty(request.DataSource) && !string.IsNullOrEmpty(request.SyncIdentifier))
        {
            var existingBySync = await Repository.FindBySyncIdentifierAsync(
                request.DataSource, request.SyncIdentifier, ct);
            if (existingBySync is not null)
                return Ok(existingBySync);
        }

        var (insulin, insulinProblem) = await ResolveInsulinAsync(request.PatientInsulinId, request.Timestamp, ct);
        if (insulinProblem is not null)
            return insulinProblem;

        var model = MapCreateToModel(request);
        model.InsulinContext = insulin is null ? null : BuildContext(insulin);

        var created = await Repository.CreateAsync(model, WriteOrigin.Live, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <inheritdoc/>
    public override async Task<ActionResult<BasalInjection>> Update(
        Guid id, [FromBody] UpdateBasalInjectionRequest request, CancellationToken ct = default)
    {
        var existing = await Repository.GetByIdAsync(id, ct);
        if (existing is null)
            return NotFound();

        if (ValidateUnitsAndTimestamp(request.Units, request.Timestamp) is { } unitsOrTsProblem)
            return unitsOrTsProblem;

        var (insulin, insulinProblem) = await ResolveInsulinAsync(request.PatientInsulinId, request.Timestamp, ct);
        if (insulinProblem is not null)
            return insulinProblem;

        var model = MapUpdateToModel(id, request, existing);
        model.InsulinContext = insulin is null ? null : BuildContext(insulin);

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

    /// <summary>Maps a <see cref="CreateBasalInjectionRequest"/> to a new <see cref="BasalInjection"/>.</summary>
    /// <param name="request">The inbound create request.</param>
    /// <returns>A new <see cref="BasalInjection"/> with all fields populated; <see cref="BasalInjection.CorrelationId"/> defaults to a new UUID v7 when not supplied. <see cref="BasalInjection.InsulinContext"/> is populated by the caller after PatientInsulin resolution.</returns>
    protected override BasalInjection MapCreateToModel(CreateBasalInjectionRequest request) => new()
    {
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        SyncIdentifier = request.SyncIdentifier,
        Units = request.Units,
        Notes = request.Notes,
        CorrelationId = request.CorrelationId ?? Guid.CreateVersion7(),
    };

    /// <summary>Maps an <see cref="UpdateBasalInjectionRequest"/> onto a <see cref="BasalInjection"/>, preserving immutable fields from the existing record.</summary>
    /// <param name="id">The record ID to carry forward.</param>
    /// <param name="request">The inbound update request.</param>
    /// <param name="existing">The existing record; <c>LegacyId</c> and <c>CreatedAt</c> are copied from here, and <c>CorrelationId</c> falls back to it when the request does not supply one.</param>
    /// <returns>A fully-populated <see cref="BasalInjection"/> ready for persistence. <see cref="BasalInjection.InsulinContext"/> is populated by the caller after PatientInsulin resolution.</returns>
    protected override BasalInjection MapUpdateToModel(
        Guid id, UpdateBasalInjectionRequest request, BasalInjection existing) => new()
    {
        Id = id,
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        SyncIdentifier = request.SyncIdentifier,
        Units = request.Units,
        Notes = request.Notes,
        CorrelationId = request.CorrelationId ?? existing.CorrelationId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
    };

    /// <summary>
    /// Create or update basal injections in bulk (max 1000).
    /// </summary>
    /// <remarks>
    /// Array semantics are per-item upsert, not all-or-nothing: each injection carrying both
    /// `dataSource` and `syncIdentifier` updates the row already matched by that pair; all others
    /// insert. Every item is validated with the same rules as the single create; validation
    /// failures reject the whole request with `400 Bad Request` before anything is persisted.
    /// </remarks>
    [HttpPost("bulk")]
    [RequireDeclaredWriteScope]
    [ProducesResponseType(typeof(BasalInjection[]), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BasalInjection[]>> CreateBasalInjectionsBulk(
        [FromBody] CreateBasalInjectionRequest[] requests,
        CancellationToken ct = default)
    {
        if (requests is not { Length: > 0 })
            return Problem(detail: "Basal injection data is required", statusCode: 400, title: "Bad Request");

        if (requests.Length > 1000)
            return Problem(detail: "Bulk operations are limited to 1000 injections per request", statusCode: 400, title: "Bad Request");

        if (requests.Any(r => !string.IsNullOrEmpty(r.SyncIdentifier) && string.IsNullOrEmpty(r.DataSource)))
            return Problem(detail: "DataSource is required when SyncIdentifier is supplied", statusCode: 400, title: "Bad Request");

        var models = new List<BasalInjection>(requests.Length);
        foreach (var request in requests)
        {
            if (ValidateUnitsAndTimestamp(request.Units, request.Timestamp) is { } unitsOrTsProblem)
                return unitsOrTsProblem;

            // Resolved per item: the active-at-injection-time window check depends on each
            // item's timestamp, so a per-insulin cache would skip it.
            var (insulin, insulinProblem) = await ResolveInsulinAsync(request.PatientInsulinId, request.Timestamp, ct);
            if (insulinProblem is not null)
                return insulinProblem;

            var model = MapCreateToModel(request);
            model.InsulinContext = insulin is null ? null : BuildContext(insulin);
            models.Add(model);
        }

        var persisted = await Repository.BulkCreateAsync(models, WriteOrigin.Live, ct);
        return StatusCode(201, persisted.ToArray());
    }

    /// <summary>
    /// Delete a basal injection by its external sync identifier (dataSource + syncIdentifier pair).
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

        var deleted = await ((IBasalInjectionRepository)Repository).DeleteBySyncIdentifierAsync(dataSource, syncIdentifier, WriteOrigin.Live, ct);
        return deleted > 0 ? NoContent() : NotFound();
    }

    private ObjectResult? ValidateUnitsAndTimestamp(double units, DateTimeOffset timestamp)
    {
        if (units <= 0 || units > UnitsHardCeiling)
            return Problem(detail: "Units must be > 0 and <= 500.", statusCode: 400, title: "Bad Request");

        if (timestamp > DateTimeOffset.UtcNow.AddMinutes(FutureToleranceMinutes))
            return Problem(detail: "Timestamp cannot be more than 5 minutes in the future.", statusCode: 400, title: "Bad Request");

        return null;
    }

    /// <summary>
    /// Resolves the referenced <see cref="PatientInsulin"/>, or short-circuits when the request
    /// omits the reference.
    /// </summary>
    /// <param name="patientInsulinId">
    /// The requested insulin reference, or <c>null</c>. A <c>null</c> reference is not an error:
    /// resolution is skipped and both tuple members come back <c>null</c>, leaving the caller to
    /// store the injection without an insulin context (uploader parity with
    /// <see cref="BolusController"/>).
    /// </param>
    /// <param name="timestamp">Injection time, checked against the insulin's active window.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The resolved insulin and a <c>null</c> problem on success; a <c>400 Bad Request</c> problem
    /// when a supplied reference is unknown, is not a basal insulin, or was inactive at
    /// <paramref name="timestamp"/>.
    /// </returns>
    private async Task<(PatientInsulin? Insulin, ObjectResult? Problem)> ResolveInsulinAsync(
        Guid? patientInsulinId, DateTimeOffset timestamp, CancellationToken ct)
    {
        if (patientInsulinId is not { } insulinId)
            return (null, null);

        var insulin = await insulinRepo.GetByIdAsync(insulinId, ct);
        if (insulin is null)
            return (null, Problem(detail: "PatientInsulin not found.", statusCode: 400, title: "Bad Request"));

        if (insulin.Role != InsulinRole.Basal && insulin.Role != InsulinRole.Both)
            return (null, Problem(detail: "Referenced insulin is not a basal insulin.", statusCode: 400, title: "Bad Request"));

        var injectionDate = DateOnly.FromDateTime(timestamp.UtcDateTime);
        if ((insulin.StartDate is { } start && start > injectionDate)
            || (insulin.EndDate is { } end && end < injectionDate))
        {
            return (null, Problem(
                detail: "Referenced insulin was not active at injection time.",
                statusCode: 400, title: "Bad Request"));
        }

        return (insulin, null);
    }

    private static TreatmentInsulinContext BuildContext(PatientInsulin insulin) => new()
    {
        PatientInsulinId = insulin.Id,
        InsulinName = insulin.Name,
        Dia = insulin.Dia,
        Peak = insulin.Peak,
        Curve = insulin.Curve,
        Concentration = insulin.Concentration,
    };
}
