using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Treatments;
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
/// Controller for managing nutrition data: carbohydrate intakes, food breakdown, and meals.
/// </summary>
/// <remarks>
/// Three logical resource groups are exposed under <c>/api/v4/nutrition</c>:
/// <list type="bullet">
///   <item><description><b>Carb Intakes</b> (<c>/carbs</c>) — standard CRUD for <see cref="CarbIntake"/> records backed by <see cref="ICarbIntakeRepository"/>.</description></item>
///   <item><description><b>Food Breakdown</b> (<c>/carbs/{id}/foods</c>) — per-carb-intake food attribution lines managed via <see cref="ITreatmentFoodService"/>.</description></item>
///   <item><description><b>Meals</b> (<c>/meals</c>) — atomic creation of a correlated <see cref="Bolus"/> + <see cref="CarbIntake"/> pair, and event-centric meal retrieval grouped by <c>CorrelationId</c>.</description></item>
/// </list>
///
/// The <c>POST /meals</c> endpoint is idempotent on <c>(DataSource, SyncIdentifier)</c>: if a matching
/// bolus already exists its <c>CorrelationId</c> is propagated to both records. The two repositories
/// write on their own contexts, so <see cref="GuardRecreationAsync"/> is what keeps a refused meal
/// from writing one half.
///
/// Meal retrieval, including its demo-mode filtering, is <see cref="IMealReadService"/>.
/// </remarks>
/// <seealso cref="ICarbIntakeRepository"/>
/// <seealso cref="IBolusRepository"/>
/// <seealso cref="ITreatmentFoodService"/>
[ApiController]
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

    #region Carb Intakes

    /// <summary>
    /// Get carb intakes with optional filtering
    /// </summary>
    /// <param name="from">Inclusive start of the date range filter.</param>
    /// <param name="to">Inclusive end of the date range filter.</param>
    /// <param name="limit">Maximum number of records to return. Defaults to `100`.</param>
    /// <param name="offset">Number of records to skip for pagination. Defaults to `0`.</param>
    /// <param name="sort">Sort order by timestamp: `timestamp_asc` or `timestamp_desc`. Defaults to `timestamp_desc`.</param>
    /// <param name="device">Optional filter to restrict results to a specific device.</param>
    /// <param name="source">Optional filter to restrict results to a specific data source.</param>
    /// <param name="includeTotal">Count every record in the range and return it as `pagination.total`. Defaults to `false`.</param>
    /// <param name="ct">Cancels the operation.</param>
    [HttpGet("carbs")]
    [RemoteQuery]
    [ProducesResponseType(typeof(PaginatedResponse<CarbIntake>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<CarbIntake>>> GetCarbIntakes(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        [FromQuery] bool includeTotal = false,
        CancellationToken ct = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");

        limit = V4ReadLimits.ClampLimit(limit);
        offset = V4ReadLimits.ClampOffset(offset);

        var descending = sort == "timestamp_desc";
        var data = (await _carbIntakeRepo.GetAsync(from, to, device, source, limit, offset, descending, ct: ct)).ToList();
        var pagination = await PaginationInfo.ForPageAsync(limit, offset, data.Count, includeTotal, () => _carbIntakeRepo.CountAsync(from, to, ct));
        return Ok(new PaginatedResponse<CarbIntake> { Data = data, Pagination = pagination });
    }

    /// <summary>
    /// Get a carb intake by ID
    /// </summary>
    [HttpGet("carbs/{id:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(CarbIntake), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CarbIntake>> GetCarbIntakeById(Guid id, CancellationToken ct = default)
    {
        var result = await _carbIntakeRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Create a new carb intake
    /// </summary>
    [HttpPost("carbs")]
    [RequireDeclaredWriteScope]
    [RemoteForm(Invalidates = ["GetCarbIntakes"])]
    [ProducesResponseType(typeof(CarbIntake), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CarbIntake>> CreateCarbIntake([FromBody] CreateCarbIntakeRequest request, CancellationToken ct = default)
    {
        if (request.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");

        var model = MapCreateToModel(request);

        var created = await _carbIntakeRepo.CreateAsync(model, WriteOrigin.Live, ct);
        return CreatedAtAction(nameof(GetCarbIntakeById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Create or update carb intakes in bulk (max 1000).
    /// An intake with both `dataSource` and `syncIdentifier` updates the existing row matching that pair; any other intake is inserted. An item whose pair is held by a deleted record is skipped and left out of the response.
    /// A validation failure rejects the whole request with `400 Bad Request` and stores nothing.
    /// </summary>
    /// <remarks>
    /// Array semantics are per-item upsert, not all-or-nothing: each intake carrying both
    /// `dataSource` and `syncIdentifier` updates the row already matched by that pair; all others
    /// insert. Validation failures reject the whole request with `400 Bad Request` before anything
    /// is persisted.
    /// </remarks>
    [HttpPost("carbs/bulk")]
    [RequireDeclaredWriteScope]
    [ProducesResponseType(typeof(CarbIntake[]), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CarbIntake[]>> CreateCarbIntakesBulk(
        [FromBody] CreateCarbIntakeRequest[] requests,
        CancellationToken ct = default)
    {
        if (await this.ValidateBulkAsync(requests, "Carb intake", "intake", "intakes", ct) is { } invalid)
            return invalid;

        var models = requests.Select(MapCreateToModel).ToList();
        var persisted = await _carbIntakeRepo.BulkCreateAsync(models, WriteOrigin.Live, ct);
        return StatusCode(201, persisted.ToArray());
    }

    private static CarbIntake MapCreateToModel(CreateCarbIntakeRequest request) => new()
    {
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        Carbs = request.Carbs,
        SyncIdentifier = request.SyncIdentifier,
        CarbTime = request.CarbTime,
        AbsorptionTime = request.AbsorptionTime,
        FatGrams = request.FatGrams,
        ProteinGrams = request.ProteinGrams,
        // Records split from one source share a correlation_id; it is a free-standing
        // grouping value (no batch row to persist), so honour a client-supplied id or mint one.
        CorrelationId = request.CorrelationId ?? Guid.CreateVersion7(),
    };

    /// <summary>
    /// Update an existing carb intake
    /// </summary>
    [HttpPut("carbs/{id:guid}")]
    [RequireDeclaredWriteScope]
    [RemoteForm(Invalidates = ["GetCarbIntakes", "GetCarbIntakeById"])]
    [ProducesResponseType(typeof(CarbIntake), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CarbIntake>> UpdateCarbIntake(Guid id, [FromBody] UpdateCarbIntakeRequest request, CancellationToken ct = default)
    {
        if (request.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");

        var existing = await _carbIntakeRepo.GetByIdAsync(id, ct);
        if (existing is null)
            return NotFound();

        var model = new CarbIntake
        {
            Id = id,
            Timestamp = request.Timestamp.UtcDateTime,
            UtcOffset = request.UtcOffset,
            Device = request.Device,
            App = request.App,
            DataSource = request.DataSource,
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
        };

        try
        {
            var updated = await _carbIntakeRepo.UpdateAsync(id, model, WriteOrigin.Live, ct);
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Delete a carb intake
    /// </summary>
    [HttpDelete("carbs/{id:guid}")]
    [RequireDeclaredWriteScope]
    [RemoteCommand(Invalidates = ["GetCarbIntakes"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteCarbIntake(Guid id, CancellationToken ct = default)
    {
        try
        {
            await _carbIntakeRepo.DeleteAsync(id, WriteOrigin.Live, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Delete a carb intake by its external sync identifier (dataSource + syncIdentifier pair).
    /// </summary>
    [HttpDelete("carbs/by-sync-id")]
    [RequireDeclaredWriteScope]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> DeleteCarbIntakeBySyncIdentifier(
        [FromQuery] string dataSource,
        [FromQuery] string syncIdentifier,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(dataSource) || string.IsNullOrEmpty(syncIdentifier))
            return BadRequest("dataSource and syncIdentifier are required");

        var deleted = await _carbIntakeRepo.DeleteBySyncIdentifierAsync(dataSource, syncIdentifier, WriteOrigin.Live, ct);
        return deleted > 0 ? NoContent() : NotFound();
    }

    #endregion

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
    [RemoteForm(Invalidates = ["GetCarbIntakes", "GetMeals"])]
    [ProducesResponseType(typeof(CreateMealResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(CreateMealResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreateMealResponse>> CreateMeal(
        [FromBody] CreateMealRequest request,
        CancellationToken ct = default)
    {
        if (request.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");

        // The meal's bolus + carb intake share a correlation_id; it is a free-standing
        // grouping value (no batch row to persist), so honour a client-supplied id or mint one.
        var correlationId = request.CorrelationId ?? Guid.CreateVersion7();
        var timestamp = request.Timestamp.UtcDateTime;

        var bolusModel = new Bolus
        {
            Timestamp = timestamp,
            UtcOffset = request.UtcOffset,
            Device = request.Device,
            App = request.App,
            DataSource = request.DataSource,
            Insulin = request.Insulin,
            BolusType = request.BolusType,
            Kind = BolusKind.Manual,
            Duration = request.Duration,
            SyncIdentifier = request.SyncIdentifier,
            InsulinType = request.InsulinType,
            BolusCalculationId = request.BolusCalculationId,
            CorrelationId = correlationId,
        };

        var carbModel = new CarbIntake
        {
            Timestamp = timestamp,
            UtcOffset = request.UtcOffset,
            Device = request.Device,
            App = request.App,
            DataSource = request.DataSource,
            Carbs = request.Carbs,
            SyncIdentifier = request.SyncIdentifier,
            CarbTime = request.CarbTime,
            AbsorptionTime = request.AbsorptionTime,
            CorrelationId = correlationId,
        };

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
    public async Task<ActionResult<MealEvent[]>> GetMeals(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] bool? attributed = null,
        CancellationToken ct = default)
    {
        var fromTimestamp = from ?? DateTime.UtcNow.Date;
        var toTimestamp = to ?? DateTime.UtcNow.Date.AddDays(1);

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
