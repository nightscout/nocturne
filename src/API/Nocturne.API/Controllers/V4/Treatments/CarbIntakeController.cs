using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Controllers.V4.Treatments;

/// <summary>
/// Carbohydrate intakes. List, read, create (one or in bulk), update and delete carb intakes. List
/// deleted carb intakes and restore them. Delete a carb intake by its data source and sync identifier.
/// </summary>
/// <remarks>
/// Standard V4 CRUD operations via <see cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>.
/// Every intake a read, update, bulk create or restore returns carries <see cref="CarbIntake.FoodType"/>,
/// read from its food lines, except when the enrichment of a committed write fails (see
/// <see cref="V4ReadOnlyControllerBase{TModel,TRepository}.OnBeforeReturnAsync"/>). Create answers with the
/// intake as stored.
///
/// On update, <see cref="V4RecordBase.LegacyId"/>, <see cref="V4RecordBase.CreatedAt"/> and
/// <see cref="V4RecordBase.AdditionalProperties"/> are preserved from the existing record.
/// <see cref="V4RecordBase.CorrelationId"/> falls back to the existing value if the request does not
/// supply one.
///
/// The food lines of an intake and meals are <see cref="NutritionController"/>.
/// </remarks>
/// <seealso cref="ICarbIntakeRepository"/>
/// <seealso cref="CarbIntake"/>
/// <seealso cref="CreateCarbIntakeRequest"/>
/// <seealso cref="UpdateCarbIntakeRequest"/>
[ApiController]
[System.ComponentModel.DisplayName("Carb Intakes")]
[Tags("Treatments")]
[Route("api/v4/nutrition/carbs")]
[RequireScope(Scope.TreatmentsRead)]
[Produces("application/json")]
[ClearsTenantCache(TenantCacheFamily.Statistics)]
public class CarbIntakeController(ICarbIntakeRepository repo, ITreatmentFoodService treatmentFoodService)
    : V4CrudControllerBase<CarbIntake, CreateCarbIntakeRequest, UpdateCarbIntakeRequest, ICarbIntakeRepository>(repo)
{
    /// <inheritdoc/>
    /// <remarks>Carb intakes are treatments; the legacy equivalent is a v1 carb treatment.</remarks>
    public override string WriteScope => Scope.TreatmentsReadWrite;

    /// <inheritdoc/>
    protected override V4BulkNaming BulkNaming => new("Carb intake", "intakes");

    /// <inheritdoc/>
    /// <remarks>
    /// Overridden, as are <see cref="Update"/> and <see cref="Delete"/>, only to name the queries its
    /// generated remote function refreshes, which the base actions leave undeclared.
    /// </remarks>
    [RemoteForm(Invalidates = ["GetAll"])]
    public override Task<ActionResult<CarbIntake>> Create(
        [FromBody] CreateCarbIntakeRequest request, CancellationToken ct = default)
        => base.Create(request, ct);

    /// <inheritdoc/>
    [RemoteForm(Invalidates = ["GetAll", "GetById"])]
    public override Task<ActionResult<CarbIntake>> Update(
        Guid id, [FromBody] UpdateCarbIntakeRequest request, CancellationToken ct = default)
        => base.Update(id, request, ct);

    /// <inheritdoc/>
    [RemoteCommand(Invalidates = ["GetAll"])]
    public override Task<ActionResult> Delete(Guid id, CancellationToken ct = default)
        => base.Delete(id, ct);

    /// <inheritdoc/>
    protected override Task OnBeforeReturnAsync(IReadOnlyCollection<CarbIntake> records, CancellationToken ct)
        => treatmentFoodService.ApplyFoodTypesAsync(records, ct);

    protected override CarbIntake MapCreateToModel(CreateCarbIntakeRequest request) => new CarbIntake
    {
        Carbs = request.Carbs,
        SyncIdentifier = request.SyncIdentifier,
        CarbTime = request.CarbTime,
        AbsorptionTime = request.AbsorptionTime,
        FatGrams = request.FatGrams,
        ProteinGrams = request.ProteinGrams,
        // Records split from one source share a correlation_id; it is a free-standing
        // grouping value (no batch row to persist), so honour a client-supplied id or mint one.
        CorrelationId = request.CorrelationId ?? Guid.CreateVersion7(),
    }.WithHeaderFrom(request);

    protected override CarbIntake MapUpdateToModel(Guid id, UpdateCarbIntakeRequest request, CarbIntake existing) => new CarbIntake
    {
        Id = id,
        Carbs = request.Carbs,
        SyncIdentifier = request.SyncIdentifier,
        CarbTime = request.CarbTime,
        AbsorptionTime = request.AbsorptionTime,
        FatGrams = request.FatGrams,
        ProteinGrams = request.ProteinGrams,
        CorrelationId = request.CorrelationId ?? existing.CorrelationId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
        AdditionalProperties = existing.AdditionalProperties,
    }.WithHeaderFrom(request);

    /// <summary>
    /// Delete a carb intake by its external sync identifier (dataSource + syncIdentifier pair).
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

        var deleted = await Repository.DeleteBySyncIdentifierAsync(dataSource, syncIdentifier, WriteOrigin.Live, ct);
        return deleted > 0 ? NoContent() : NotFound();
    }
}
