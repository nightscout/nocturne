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
/// <see cref="BasalInjection.Timestamp"/> must be set and no more than five minutes in the future, and —
/// when the request carries a <c>PatientInsulinId</c> — the referenced <see cref="PatientInsulin"/> must
/// exist with role <see cref="InsulinRole.Basal"/> or <see cref="InsulinRole.Both"/> and be active at the
/// injection time. The server resolves <see cref="PatientInsulin"/> fresh on every write to populate the
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
    protected override V4BulkNaming BulkNaming => new("Basal injection", "injection", "injections");

    /// <inheritdoc/>
    protected override async Task<ObjectResult?> OnBeforeCreateAsync(
        BasalInjection model, CreateBasalInjectionRequest request, CancellationToken ct)
    {
        if (ValidateUnitsAndFutureTolerance(request.Units, request.Timestamp) is { } unitsOrTsProblem)
            return unitsOrTsProblem;

        // Idempotent upsert: a record already stored under this (DataSource, SyncIdentifier) is
        // returned as a 200 instead of the create the caller asked for, so the hook short-circuits
        // with a success result rather than a rejection.
        if (!string.IsNullOrEmpty(request.DataSource) && !string.IsNullOrEmpty(request.SyncIdentifier)
            && await Repository.FindBySyncIdentifierAsync(request.DataSource, request.SyncIdentifier, ct) is { } existingBySync)
        {
            return Ok(existingBySync);
        }

        return await ApplyInsulinContextAsync(model, request.PatientInsulinId, request.Timestamp, ct);
    }

    /// <inheritdoc/>
    protected override Task<ObjectResult?> OnBeforeUpdateAsync(
        BasalInjection model, UpdateBasalInjectionRequest request, BasalInjection existing, CancellationToken ct)
        => ValidateUnitsAndFutureTolerance(request.Units, request.Timestamp) is { } unitsOrTsProblem
            ? Task.FromResult<ObjectResult?>(unitsOrTsProblem)
            : ApplyInsulinContextAsync(model, request.PatientInsulinId, request.Timestamp, ct);

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

    /// <inheritdoc/>
    protected override async Task<ObjectResult?> OnBeforeBulkCreateAsync(
        IReadOnlyList<BasalInjection> models, IReadOnlyList<CreateBasalInjectionRequest> requests, CancellationToken ct)
    {
        for (var i = 0; i < models.Count; i++)
        {
            if (ValidateUnitsAndFutureTolerance(requests[i].Units, requests[i].Timestamp) is { } unitsOrTsProblem)
                return unitsOrTsProblem;

            // Resolved per item: the active-at-injection-time window check depends on each
            // item's timestamp, so a per-insulin cache would skip it.
            if (await ApplyInsulinContextAsync(models[i], requests[i].PatientInsulinId, requests[i].Timestamp, ct) is { } insulinProblem)
                return insulinProblem;
        }

        return null;
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

    /// <summary>
    /// The rules the CRUD base does not already enforce: the unset-timestamp guard is the base's,
    /// on every single and bulk write path alike.
    /// </summary>
    private ObjectResult? ValidateUnitsAndFutureTolerance(double units, DateTimeOffset timestamp)
    {
        if (units <= 0 || units > UnitsHardCeiling)
            return Problem(detail: "Units must be > 0 and <= 500.", statusCode: 400, title: "Bad Request");

        if (timestamp > DateTimeOffset.UtcNow.AddMinutes(FutureToleranceMinutes))
            return Problem(detail: "Timestamp cannot be more than 5 minutes in the future.", statusCode: 400, title: "Bad Request");

        return null;
    }

    /// <summary>
    /// Resolves the referenced <see cref="PatientInsulin"/> onto
    /// <see cref="BasalInjection.InsulinContext"/>, or leaves it <c>null</c> when the request omits
    /// the reference — not an error, but uploader parity with <see cref="BolusController"/> for
    /// clients that know nothing about the patient's insulin catalog.
    /// </summary>
    /// <param name="model">The mapped injection, enriched in place.</param>
    /// <param name="patientInsulinId">The requested insulin reference, or <c>null</c>.</param>
    /// <param name="timestamp">Injection time, checked against the insulin's active window.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>null</c> on success; a <c>400 Bad Request</c> problem when a supplied reference is
    /// unknown, is not a basal insulin, or was inactive at <paramref name="timestamp"/>.
    /// </returns>
    private async Task<ObjectResult?> ApplyInsulinContextAsync(
        BasalInjection model, Guid? patientInsulinId, DateTimeOffset timestamp, CancellationToken ct)
    {
        model.InsulinContext = null;

        if (patientInsulinId is not { } insulinId)
            return null;

        var insulin = await insulinRepo.GetByIdAsync(insulinId, ct);
        if (insulin is null)
            return Problem(detail: "PatientInsulin not found.", statusCode: 400, title: "Bad Request");

        if (insulin.Role != InsulinRole.Basal && insulin.Role != InsulinRole.Both)
            return Problem(detail: "Referenced insulin is not a basal insulin.", statusCode: 400, title: "Bad Request");

        var injectionDate = DateOnly.FromDateTime(timestamp.UtcDateTime);
        if ((insulin.StartDate is { } start && start > injectionDate)
            || (insulin.EndDate is { } end && end < injectionDate))
        {
            return Problem(
                detail: "Referenced insulin was not active at injection time.",
                statusCode: 400, title: "Bad Request");
        }

        model.InsulinContext = new TreatmentInsulinContext
        {
            PatientInsulinId = insulin.Id,
            InsulinName = insulin.Name,
            Dia = insulin.Dia,
            Peak = insulin.Peak,
            Curve = insulin.Curve,
            Concentration = insulin.Concentration,
        };

        return null;
    }
}
