using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Treatments;

/// <summary>
/// Controller for managing bolus calculation records.
/// Exposes standard V4 CRUD operations via <see cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>.
/// </summary>
/// <remarks>
/// Create and update use the same <see cref="UpsertBolusCalculationRequest"/> shape.
/// On update, the immutable fields <see cref="BolusCalculation.CorrelationId"/>,
/// <see cref="BolusCalculation.LegacyId"/>, <see cref="BolusCalculation.CreatedAt"/>,
/// and <see cref="BolusCalculation.AdditionalProperties"/> are preserved from the existing record.
/// </remarks>
/// <seealso cref="IBolusCalculationRepository"/>
/// <seealso cref="BolusCalculation"/>
/// <seealso cref="UpsertBolusCalculationRequest"/>
[ApiController]
[Tags("Treatments")]
[Route("api/v4/insulin/calculations")]
[RequireScope(Scope.TreatmentsRead)]
[Produces("application/json")]
public class BolusCalculationController(IBolusCalculationRepository repo)
    : V4CrudControllerBase<BolusCalculation, UpsertBolusCalculationRequest, UpsertBolusCalculationRequest, IBolusCalculationRepository>(repo)
{
    /// <inheritdoc/>
    /// <remarks>Bolus calculations sit in the treatments share category alongside the boluses they explain.</remarks>
    public override string WriteScope => Scope.TreatmentsReadWrite;

    protected override BolusCalculation MapCreateToModel(UpsertBolusCalculationRequest request) => new()
    {
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        BloodGlucoseInput = request.BloodGlucoseInput,
        BloodGlucoseInputSource = request.BloodGlucoseInputSource,
        CarbInput = request.CarbInput,
        InsulinOnBoard = request.InsulinOnBoard,
        InsulinRecommendation = request.InsulinRecommendation,
        CarbRatio = request.CarbRatio,
        CalculationType = request.CalculationType,
        InsulinRecommendationForCarbs = request.InsulinRecommendationForCarbs,
        InsulinProgrammed = request.InsulinProgrammed,
        EnteredInsulin = request.EnteredInsulin,
        SplitNow = request.SplitNow,
        SplitExt = request.SplitExt,
        PreBolus = request.PreBolus,
    };

    protected override BolusCalculation MapUpdateToModel(Guid id, UpsertBolusCalculationRequest request, BolusCalculation existing) => new()
    {
        Id = id,
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        BloodGlucoseInput = request.BloodGlucoseInput,
        BloodGlucoseInputSource = request.BloodGlucoseInputSource,
        CarbInput = request.CarbInput,
        InsulinOnBoard = request.InsulinOnBoard,
        InsulinRecommendation = request.InsulinRecommendation,
        CarbRatio = request.CarbRatio,
        CalculationType = request.CalculationType,
        InsulinRecommendationForCarbs = request.InsulinRecommendationForCarbs,
        InsulinProgrammed = request.InsulinProgrammed,
        EnteredInsulin = request.EnteredInsulin,
        SplitNow = request.SplitNow,
        SplitExt = request.SplitExt,
        PreBolus = request.PreBolus,
        CorrelationId = existing.CorrelationId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
        AdditionalProperties = existing.AdditionalProperties,
    };
}
