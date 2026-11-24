using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Treatments;

/// <summary>
/// Bolus calculation records. List, read, create (one or in bulk), update and delete calculations.
/// List deleted calculations and restore them.
/// </summary>
/// <remarks>
/// Standard V4 CRUD operations via <see cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>.
///
/// Create and update use the same <see cref="UpsertBolusCalculationRequest"/> shape.
/// On update, the immutable fields <see cref="V4RecordBase.CorrelationId"/>,
/// <see cref="V4RecordBase.LegacyId"/>, <see cref="V4RecordBase.CreatedAt"/>,
/// and <see cref="V4RecordBase.AdditionalProperties"/> are preserved from the existing record.
/// </remarks>
/// <seealso cref="IBolusCalculationRepository"/>
/// <seealso cref="BolusCalculation"/>
/// <seealso cref="UpsertBolusCalculationRequest"/>
[ApiController]
[System.ComponentModel.DisplayName("Bolus Calculations")]
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

    /// <inheritdoc/>
    protected override V4BulkNaming BulkNaming => new("Bolus calculation", "calculations");

    protected override BolusCalculation MapCreateToModel(UpsertBolusCalculationRequest request) => new BolusCalculation
    {
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
    }.WithHeaderFrom(request);

    protected override BolusCalculation MapUpdateToModel(Guid id, UpsertBolusCalculationRequest request, BolusCalculation existing) => new BolusCalculation
    {
        Id = id,
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
    }.WithHeaderFrom(request);
}
