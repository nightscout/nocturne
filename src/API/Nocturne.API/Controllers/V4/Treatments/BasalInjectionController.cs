using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Treatments;

/// <summary>
/// CRUD for long-acting basal insulin injections (MDI).
/// Exposes standard V4 CRUD operations via <see cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>,
/// with additional validation and idempotent upsert on (<see cref="BasalInjection.DataSource"/>, <see cref="BasalInjection.SyncIdentifier"/>).
/// </summary>
/// <remarks>
/// Both create and update enforce the same rules: <see cref="BasalInjection.Units"/> must be in (0, 500],
/// <see cref="BasalInjection.Timestamp"/> may not be more than five minutes in the future, the referenced
/// <see cref="PatientInsulin"/> must exist with role <see cref="InsulinRole.Basal"/> or <see cref="InsulinRole.Both"/>,
/// and the insulin must be active at the injection time. The server resolves <see cref="PatientInsulin"/>
/// fresh on every write to populate the <see cref="TreatmentInsulinContext"/> snapshot.
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
[Authorize]
[Produces("application/json")]
public class BasalInjectionController(
    IBasalInjectionRepository repo,
    IPatientInsulinRepository insulinRepo)
    : V4CrudControllerBase<BasalInjection, CreateBasalInjectionRequest, UpdateBasalInjectionRequest, IBasalInjectionRepository>(repo)
{
    private const double UnitsHardCeiling = 500.0;
    private const int FutureToleranceMinutes = 5;

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
        model.InsulinContext = BuildContext(insulin!);

        var created = await Repository.CreateAsync(model, ct);
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
        model.InsulinContext = BuildContext(insulin!);

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

    private ObjectResult? ValidateUnitsAndTimestamp(double units, DateTimeOffset timestamp)
    {
        if (units <= 0 || units > UnitsHardCeiling)
            return Problem(detail: "Units must be > 0 and <= 500.", statusCode: 400, title: "Bad Request");

        if (timestamp > DateTimeOffset.UtcNow.AddMinutes(FutureToleranceMinutes))
            return Problem(detail: "Timestamp cannot be more than 5 minutes in the future.", statusCode: 400, title: "Bad Request");

        return null;
    }

    private async Task<(PatientInsulin? Insulin, ObjectResult? Problem)> ResolveInsulinAsync(
        Guid patientInsulinId, DateTimeOffset timestamp, CancellationToken ct)
    {
        var insulin = await insulinRepo.GetByIdAsync(patientInsulinId, ct);
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
