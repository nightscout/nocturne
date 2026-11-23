using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Validators.V4;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;
using Nocturne.API.Services.Caching;

namespace Nocturne.API.Controllers.V4.Treatments;

/// <summary>
/// The foods in each carb intake, and meals. Create, read, update, and delete the food lines of a carb
/// intake. Create a meal, and list meals.
/// </summary>
/// <remarks>
/// Two logical resource groups are exposed under <c>/api/v4/nutrition</c>; the carb intakes
/// themselves are <see cref="CarbIntakeController"/>:
/// <list type="bullet">
///   <item><description><b>Food Breakdown</b> (<c>/carbs/{id}/foods</c>) — per-carb-intake food attribution lines managed via <see cref="ITreatmentFoodService"/>.</description></item>
///   <item><description><b>Meals</b> (<c>/meals</c>) — atomic creation of a correlated <see cref="Bolus"/> + <see cref="CarbIntake"/> pair, and event-centric meal retrieval grouped by <c>CorrelationId</c>.</description></item>
/// </list>
///
/// The <c>POST /meals</c> endpoint is idempotent on <c>(DataSource, SyncIdentifier)</c>: if a matching
/// bolus already exists its <c>CorrelationId</c> is propagated to both records. The two repositories
/// write on their own contexts, so <see cref="GuardRecreationAsync"/> is what keeps a refused meal
/// from writing one half. The food types of the carb intake of an existing meal are filled through
/// <see cref="CommittedWriteEnrichment"/>, so a failure there leaves them unset and the meal is still
/// answered.
///
/// Meal retrieval, including its demo-mode filtering, is <see cref="IMealReadService"/>.
/// </remarks>
/// <seealso cref="ICarbIntakeRepository"/>
/// <seealso cref="IBolusRepository"/>
/// <seealso cref="ITreatmentFoodService"/>
[ApiController]
[System.ComponentModel.DisplayName("Carb Foods and Meals")]
[Tags("Treatments")]
[Route("api/v4/nutrition")]
[RequireScope(Scope.TreatmentsRead)]
[Produces("application/json")]
[ClearsTenantCache(TenantCacheFamily.Statistics)]
public class NutritionController : ControllerBase, IWriteScopedController
{
    /// <summary>
    /// The OAuth scope every write action on this controller requires. Carb intakes, and the boluses
    /// <c>POST /meals</c> creates alongside them, are the treatments category
    /// (<see cref="ShareDataCategories"/>), gated with <c>treatments.readwrite</c> on the V1 and V3
    /// treatment write endpoints. The per-carb-intake food breakdown is keyed by carb intake and
    /// reads the food catalog without mutating it, so it is gated with the treatment it describes.
    /// </summary>
    public string WriteScope => Scope.TreatmentsReadWrite;

    private readonly ICarbIntakeRepository _carbIntakeRepo;
    private readonly IBolusRepository _bolusRepo;
    private readonly ITreatmentFoodService _treatmentFoodService;
    private readonly IFoodRepository _foodRepo;
    private readonly IMealReadService _mealReadService;

    public NutritionController(
        ICarbIntakeRepository carbIntakeRepo,
        IBolusRepository bolusRepo,
        ITreatmentFoodService treatmentFoodService,
        IFoodRepository foodRepo,
        IMealReadService mealReadService)
    {
        _carbIntakeRepo = carbIntakeRepo;
        _bolusRepo = bolusRepo;
        _treatmentFoodService = treatmentFoodService;
        _foodRepo = foodRepo;
        _mealReadService = mealReadService;
    }

    #region Carb Intake Food Breakdown

    /// <summary>
    /// Get food breakdown for a carb intake record.
    /// </summary>
    [HttpGet("carbs/{id:guid}/foods")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TreatmentFoodBreakdown), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TreatmentFoodBreakdown>> GetCarbIntakeFoods(Guid id, CancellationToken ct = default)
    {
        var breakdown = await _treatmentFoodService.GetByCarbIntakeIdAsync(id, ct);
        return breakdown is null ? NotFound() : Ok(breakdown);
    }

    /// <summary>
    /// Add a food breakdown entry to a carb intake record.
    /// </summary>
    [HttpPost("carbs/{id:guid}/foods")]
    [RequireDeclaredWriteScope]
    [RemoteCommand(Invalidates = ["GetCarbIntakeFoods"])]
    [ProducesResponseType(typeof(TreatmentFoodBreakdown), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TreatmentFoodBreakdown>> AddCarbIntakeFood(
        Guid id,
        [FromBody] CarbIntakeFoodRequest request,
        CancellationToken ct = default)
    {
        if (await _carbIntakeRepo.GetByIdAsync(id, ct) is null)
            return NotFound();

        var entry = await BuildFoodEntryAsync(request, id, null, ct);
        if (entry == null)
            return BadRequest();

        await _treatmentFoodService.AddAsync(entry, ct);

        var breakdown = await _treatmentFoodService.GetByCarbIntakeIdAsync(id, ct);
        return Ok(breakdown);
    }

    /// <summary>
    /// Update a food breakdown entry.
    /// </summary>
    [HttpPut("carbs/{id:guid}/foods/{foodEntryId:guid}")]
    [RequireDeclaredWriteScope]
    [RemoteCommand(Invalidates = ["GetCarbIntakeFoods"])]
    [ProducesResponseType(typeof(TreatmentFoodBreakdown), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TreatmentFoodBreakdown>> UpdateCarbIntakeFood(
        Guid id,
        Guid foodEntryId,
        [FromBody] CarbIntakeFoodRequest request,
        CancellationToken ct = default)
    {
        var breakdown = await _treatmentFoodService.GetByCarbIntakeIdAsync(id, ct);
        var existing = breakdown?.Foods.FirstOrDefault(f => f.Id == foodEntryId);

        if (existing == null)
            return NotFound();

        var entry = await BuildFoodEntryAsync(request, id, existing, ct);
        if (entry == null)
            return BadRequest();

        entry.Id = foodEntryId;
        var updated = await _treatmentFoodService.UpdateAsync(entry, ct);
        if (updated == null)
            return NotFound();

        var updatedBreakdown = await _treatmentFoodService.GetByCarbIntakeIdAsync(id, ct);
        return Ok(updatedBreakdown);
    }

    /// <summary>
    /// Remove a food breakdown entry.
    /// </summary>
    [HttpDelete("carbs/{id:guid}/foods/{foodEntryId:guid}")]
    [RequireDeclaredWriteScope]
    [RemoteCommand(Invalidates = ["GetCarbIntakeFoods"])]
    [ProducesResponseType(typeof(TreatmentFoodBreakdown), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TreatmentFoodBreakdown>> DeleteCarbIntakeFood(
        Guid id,
        Guid foodEntryId,
        CancellationToken ct = default)
    {
        var existingBreakdown = await _treatmentFoodService.GetByCarbIntakeIdAsync(id, ct);
        var existing = existingBreakdown?.Foods.FirstOrDefault(f => f.Id == foodEntryId);

        if (existing == null)
            return NotFound();

        await _treatmentFoodService.DeleteAsync(foodEntryId, ct);

        var breakdown = await _treatmentFoodService.GetByCarbIntakeIdAsync(id, ct);
        return Ok(breakdown);
    }

    #endregion

    #region Meals

    /// <summary>
    /// Create a correlated Bolus + CarbIntake for a meal event, sharing a single
    /// CorrelationId. When an existing row matches on (DataSource, SyncIdentifier),
    /// the idempotent upsert applies and the response returns 200 instead of 201.
    /// Either half being held by a record the owner deleted refuses the whole meal
    /// with 409 before anything is written; see <see cref="GuardRecreationAsync"/>
    /// for why that check is what makes the pair all-or-nothing.
    /// </summary>
    [HttpPost("meals")]
    [RequireDeclaredWriteScope]
    [RemoteForm(Invalidates = ["CarbIntake_GetAll", "GetMeals"])]
    [ProducesResponseType(typeof(CreateMealResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(CreateMealResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreateMealResponse>> CreateMeal(
        [FromBody] CreateMealRequest request,
        CancellationToken ct = default)
    {
        if (TimestampRule.IsMissing(request.Timestamp))
            return this.TimestampRequiredProblem();

        // The meal's bolus + carb intake share a correlation_id; it is a free-standing
        // grouping value (no batch row to persist), so honour a client-supplied id or mint one.
        var correlationId = request.CorrelationId ?? Guid.CreateVersion7();

        var bolusModel = new Bolus
        {
            Insulin = request.Insulin,
            BolusType = request.BolusType,
            Kind = BolusKind.Manual,
            Duration = request.Duration,
            SyncIdentifier = request.SyncIdentifier,
            InsulinType = request.InsulinType,
            BolusCalculationId = request.BolusCalculationId,
            CorrelationId = correlationId,
        }.WithHeaderFrom(request);

        var carbModel = new CarbIntake
        {
            Carbs = request.Carbs,
            SyncIdentifier = request.SyncIdentifier,
            CarbTime = request.CarbTime,
            AbsorptionTime = request.AbsorptionTime,
            CorrelationId = correlationId,
        }.WithHeaderFrom(request);

        if (!string.IsNullOrEmpty(request.DataSource) && !string.IsNullOrEmpty(request.SyncIdentifier))
            await GuardRecreationAsync(request.DataSource, request.SyncIdentifier, ct);

        // Peek at an existing bolus with the same (DataSource, SyncIdentifier) BEFORE
        // the upsert. If one exists, its CorrelationId is authoritative and must be
        // propagated to both records (the upsert itself will overwrite it in-place).
        Guid? existingBolusCorrelationId = null;
        if (!string.IsNullOrEmpty(bolusModel.DataSource) && !string.IsNullOrEmpty(bolusModel.SyncIdentifier))
        {
            existingBolusCorrelationId = await _bolusRepo.GetCorrelationIdBySyncKeyAsync(
                bolusModel.DataSource, bolusModel.SyncIdentifier, ct);
        }

        if (existingBolusCorrelationId.HasValue)
        {
            bolusModel.CorrelationId = existingBolusCorrelationId;
            carbModel.CorrelationId = existingBolusCorrelationId;
        }

        var bolus = await _bolusRepo.UpsertAsync(bolusModel, WriteOrigin.Live, ct);
        var carb = await _carbIntakeRepo.UpsertAsync(carbModel, WriteOrigin.Live, ct);

        if (!carb.Created)
        {
            CarbIntake[] carbs = [carb.Record];
            await CommittedWriteEnrichment.RunAsync(this, carbs, () => _treatmentFoodService.ApplyFoodTypesAsync(carbs, ct), ct);
        }

        var response = new CreateMealResponse
        {
            CorrelationId = bolus.Record.CorrelationId ?? carb.Record.CorrelationId ?? correlationId,
            Bolus = bolus.Record,
            CarbIntake = carb.Record,
        };

        return (bolus.Created || carb.Created)
            ? StatusCode(StatusCodes.Status201Created, response)
            : Ok(response);
    }

    /// <summary>
    /// Refuses the whole meal when either half's sync key is held by a record the user deleted.
    /// </summary>
    /// <remarks>
    /// Each repository writes on its own context, so the bolus write commits before the carb
    /// write is attempted and nothing can roll it back. Asking both
    /// repositories first is what keeps a key blocked on one half only from leaving a committed
    /// bolus behind the refusal.
    /// </remarks>
    /// <exception cref="RecreationBlockedException">
    /// Mapped to <c>409 Conflict</c> by <see cref="Filters.RecreationBlockedFilter"/>.
    /// </exception>
    private async Task GuardRecreationAsync(string dataSource, string syncIdentifier, CancellationToken ct)
    {
        if (await _bolusRepo.IsRecreationBlockedAsync(dataSource, syncIdentifier, ct))
            throw RecreationBlockedException.ForSyncKey(nameof(Bolus), dataSource, syncIdentifier);

        if (await _carbIntakeRepo.IsRecreationBlockedAsync(dataSource, syncIdentifier, ct))
            throw RecreationBlockedException.ForSyncKey(nameof(CarbIntake), dataSource, syncIdentifier);
    }

    /// <summary>
    /// Get meal events grouped by <c>CorrelationId</c>. Each event carries its
    /// own carb intakes, correlated boluses, food attribution rows, and
    /// aggregated totals. Carb intakes with a null <c>CorrelationId</c> become
    /// single-member events on their own (they are NOT collapsed together).
    /// </summary>
    [HttpGet("meals")]
    [RemoteQuery]
    [ProducesResponseType(typeof(MealEvent[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MealEvent[]>> GetMeals(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] bool? attributed = null,
        CancellationToken ct = default)
    {
        var fromTimestamp = from ?? DateTime.UtcNow.Date;
        var toTimestamp = to ?? DateTime.UtcNow.Date.AddDays(1);
        if (this.RejectDateSpan(fromTimestamp, toTimestamp) is { } overlong)
            return overlong;

        var meals = await _mealReadService.GetMealsAsync(fromTimestamp, toTimestamp, attributed, ct);
        return Ok(meals.ToArray());
    }

    #endregion

    #region Private Helpers

    private async Task<TreatmentFood?> BuildFoodEntryAsync(
        CarbIntakeFoodRequest request,
        Guid carbIntakeId,
        TreatmentFood? existing,
        CancellationToken ct)
    {
        var timeOffset = request.TimeOffsetMinutes ?? existing?.TimeOffsetMinutes ?? 0;
        var note = request.Note ?? existing?.Note;

        if (existing != null
            && request.FoodId == null
            && !request.Carbs.HasValue
            && !request.Portions.HasValue
            && !request.InputMode.HasValue)
        {
            return new TreatmentFood
            {
                Id = existing.Id,
                CarbIntakeId = carbIntakeId,
                FoodId = existing.FoodId,
                Portions = existing.Portions,
                Carbs = existing.Carbs,
                TimeOffsetMinutes = timeOffset,
                Note = note,
            };
        }

        Guid? foodId = existing?.FoodId;
        CatalogFoodPortion? catalogFood = null;

        if (!string.IsNullOrWhiteSpace(request.FoodId))
        {
            catalogFood = await _foodRepo.GetCatalogFoodAsync(request.FoodId, ct);

            if (catalogFood == null)
                return null;
            foodId = catalogFood.Id;
        }
        else if (foodId.HasValue)
        {
            catalogFood = await _foodRepo.GetCatalogFoodAsync(foodId.Value, ct);
        }

        var portions = request.Portions ?? existing?.Portions ?? 0m;
        var carbs = request.Carbs ?? existing?.Carbs ?? 0m;

        var inputMode = request.InputMode ?? (
            request.Carbs.HasValue && !request.Portions.HasValue
                ? CarbIntakeFoodInputMode.Carbs
                : CarbIntakeFoodInputMode.Portions);

        if (!foodId.HasValue)
        {
            return carbs <= 0m
                ? null
                : new TreatmentFood
                {
                    CarbIntakeId = carbIntakeId,
                    FoodId = null,
                    Portions = 0m,
                    Carbs = carbs,
                    TimeOffsetMinutes = timeOffset,
                    Note = note,
                };
        }

        if (catalogFood == null)
            return null;

        var carbsPerPortion = catalogFood.CarbsPerPortion.ToDecimal();

        if (inputMode == CarbIntakeFoodInputMode.Portions)
        {
            if (portions <= 0m) return null;
            carbs = Math.Round(carbsPerPortion * portions, 1, MidpointRounding.AwayFromZero);
        }
        else
        {
            if (carbs < 0m || carbsPerPortion <= 0m) return null;
            portions = Math.Round(carbs / carbsPerPortion, 2, MidpointRounding.AwayFromZero);
            if (portions <= 0m) return null;
        }

        return new TreatmentFood
        {
            CarbIntakeId = carbIntakeId,
            FoodId = foodId,
            Portions = portions,
            Carbs = carbs,
            TimeOffsetMinutes = timeOffset,
            Note = note,
        };
    }

    #endregion
}

/// <summary>
/// Request body for adding/updating a food entry on a carb intake record.
/// </summary>
public class CarbIntakeFoodRequest
{
    public string? FoodId { get; set; }
    public decimal? Portions { get; set; }
    public decimal? Carbs { get; set; }
    public int? TimeOffsetMinutes { get; set; }
    public string? Note { get; set; }
    public CarbIntakeFoodInputMode? InputMode { get; set; }
}

public enum CarbIntakeFoodInputMode
{
    Portions,
    Carbs,
}

/// <summary>
/// Response for <c>POST /api/v4/nutrition/meals</c>. Carries the shared
/// correlation id along with both halves of the persisted meal event.
/// </summary>
public class CreateMealResponse
{
    public Guid CorrelationId { get; set; }
    public Bolus Bolus { get; set; } = null!;
    public CarbIntake CarbIntake { get; set; } = null!;
}
