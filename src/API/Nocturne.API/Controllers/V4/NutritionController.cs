using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Attributes;
using Nocturne.API.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for managing nutrition data: carbohydrate intakes, food breakdown, and meals
/// </summary>
[ApiController]
[Route("api/v4/nutrition")]
[Authorize]
[Produces("application/json")]
[Tags("V4 Nutrition")]
public class NutritionController : ControllerBase
{
    private readonly ICarbIntakeRepository _carbIntakeRepo;
    private readonly ITreatmentFoodService _treatmentFoodService;
    private readonly IDemoModeService _demoModeService;
    private readonly NocturneDbContext _context;

    public NutritionController(
        ICarbIntakeRepository carbIntakeRepo,
        ITreatmentFoodService treatmentFoodService,
        IDemoModeService demoModeService,
        NocturneDbContext context)
    {
        _carbIntakeRepo = carbIntakeRepo;
        _treatmentFoodService = treatmentFoodService;
        _demoModeService = demoModeService;
        _context = context;
    }

    #region Carb Intakes

    /// <summary>
    /// Get carb intakes with optional filtering
    /// </summary>
    [HttpGet("carbs")]
    [RemoteQuery]
    [ProducesResponseType(typeof(PaginatedResponse<CarbIntake>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<CarbIntake>>> GetCarbIntakes(
        [FromQuery] long? from, [FromQuery] long? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "mills_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
    {
        if (sort is not "mills_desc" and not "mills_asc")
            return BadRequest(new { error = $"Invalid sort value '{sort}'. Must be 'mills_asc' or 'mills_desc'." });
        var descending = sort == "mills_desc";
        var data = await _carbIntakeRepo.GetAsync(from, to, device, source, limit, offset, descending, ct);
        var total = await _carbIntakeRepo.CountAsync(from, to, ct);
        return Ok(new PaginatedResponse<CarbIntake> { Data = data, Pagination = new(limit, offset, total) });
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
    [RemoteCommand(Invalidates = ["GetCarbIntakes"])]
    [ProducesResponseType(typeof(CarbIntake), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CarbIntake>> CreateCarbIntake([FromBody] CarbIntake model, CancellationToken ct = default)
    {
        if (model.Mills <= 0)
            return BadRequest(new { error = "Mills must be a positive value" });
        var created = await _carbIntakeRepo.CreateAsync(model, ct);
        return CreatedAtAction(nameof(GetCarbIntakeById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing carb intake
    /// </summary>
    [HttpPut("carbs/{id:guid}")]
    [RemoteCommand(Invalidates = ["GetCarbIntakes", "GetCarbIntakeById"])]
    [ProducesResponseType(typeof(CarbIntake), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CarbIntake>> UpdateCarbIntake(Guid id, [FromBody] CarbIntake model, CancellationToken ct = default)
    {
        if (model.Mills <= 0)
            return BadRequest(new { error = "Mills must be a positive value" });
        try
        {
            var updated = await _carbIntakeRepo.UpdateAsync(id, model, ct);
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
    [RemoteCommand(Invalidates = ["GetCarbIntakes"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteCarbIntake(Guid id, CancellationToken ct = default)
    {
        try
        {
            await _carbIntakeRepo.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    #endregion

    #region Carb Intake Food Breakdown

    /// <summary>
    /// Get food breakdown for a carb intake record.
    /// </summary>
    [HttpGet("carbs/{id:guid}/foods")]
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
    [ProducesResponseType(typeof(TreatmentFoodBreakdown), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TreatmentFoodBreakdown>> AddCarbIntakeFood(
        Guid id,
        [FromBody] CarbIntakeFoodRequest request,
        CancellationToken ct = default)
    {
        var carbIntake = await _context.Set<CarbIntakeEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (carbIntake == null)
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
    [ProducesResponseType(typeof(TreatmentFoodBreakdown), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TreatmentFoodBreakdown>> UpdateCarbIntakeFood(
        Guid id,
        Guid foodEntryId,
        [FromBody] CarbIntakeFoodRequest request,
        CancellationToken ct = default)
    {
        var existing = await _context.Set<TreatmentFoodEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(tf => tf.Id == foodEntryId, ct);

        if (existing == null || existing.CarbIntakeId != id)
            return NotFound();

        var entry = await BuildFoodEntryAsync(request, id, existing, ct);
        if (entry == null)
            return BadRequest();

        entry.Id = foodEntryId;
        var updated = await _treatmentFoodService.UpdateAsync(entry, ct);
        if (updated == null)
            return NotFound();

        var breakdown = await _treatmentFoodService.GetByCarbIntakeIdAsync(id, ct);
        return Ok(breakdown);
    }

    /// <summary>
    /// Remove a food breakdown entry.
    /// </summary>
    [HttpDelete("carbs/{id:guid}/foods/{foodEntryId:guid}")]
    [ProducesResponseType(typeof(TreatmentFoodBreakdown), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TreatmentFoodBreakdown>> DeleteCarbIntakeFood(
        Guid id,
        Guid foodEntryId,
        CancellationToken ct = default)
    {
        var existing = await _context.Set<TreatmentFoodEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(tf => tf.Id == foodEntryId, ct);

        if (existing == null || existing.CarbIntakeId != id)
            return NotFound();

        await _treatmentFoodService.DeleteAsync(foodEntryId, ct);

        var breakdown = await _treatmentFoodService.GetByCarbIntakeIdAsync(id, ct);
        return Ok(breakdown);
    }

    #endregion

    #region Meals

    /// <summary>
    /// Get carb intake records with food attribution status for the meals view.
    /// </summary>
    [HttpGet("meals")]
    [ProducesResponseType(typeof(MealCarbIntake[]), StatusCodes.Status200OK)]
    public async Task<ActionResult<MealCarbIntake[]>> GetMeals(
        [FromQuery] long? from = null,
        [FromQuery] long? to = null,
        [FromQuery] bool? attributed = null,
        CancellationToken ct = default)
    {
        var fromMills = from ?? new DateTimeOffset(DateTime.UtcNow.Date).ToUnixTimeMilliseconds();
        var toMills = to ?? new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1)).ToUnixTimeMilliseconds();

        var query = _context.Set<CarbIntakeEntity>()
            .AsNoTracking()
            .Where(c => c.Mills >= fromMills && c.Mills <= toMills && c.Carbs > 0);

        if (_demoModeService.IsEnabled)
            query = query.Where(c => c.DataSource == DataSources.DemoService);
        else
            query = query.Where(c => c.DataSource != DataSources.DemoService);

        var carbIntakeEntities = await query
            .OrderByDescending(c => c.Mills)
            .ToListAsync(ct);

        if (carbIntakeEntities.Count == 0)
            return Ok(Array.Empty<MealCarbIntake>());

        var carbIntakeIds = carbIntakeEntities.Select(c => c.Id).ToList();
        var foodEntries = await _treatmentFoodService.GetByCarbIntakeIdsAsync(carbIntakeIds, ct);
        var foodsByCarbIntake = foodEntries
            .GroupBy(f => f.CarbIntakeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Look up correlated boluses
        var correlationIds = carbIntakeEntities
            .Where(c => c.CorrelationId.HasValue)
            .Select(c => c.CorrelationId!.Value)
            .Distinct()
            .ToList();

        var correlatedBoluses = correlationIds.Count > 0
            ? await _context.Set<BolusEntity>()
                .AsNoTracking()
                .Where(b => b.CorrelationId.HasValue && correlationIds.Contains(b.CorrelationId.Value))
                .ToListAsync(ct)
            : [];

        var bolusByCorrelationId = correlatedBoluses
            .Where(b => b.CorrelationId.HasValue)
            .GroupBy(b => b.CorrelationId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var results = new List<MealCarbIntake>();

        foreach (var entity in carbIntakeEntities)
        {
            var foods = foodsByCarbIntake.TryGetValue(entity.Id, out var list)
                ? list
                : [];
            var attributedCarbs = foods.Sum(f => f.Carbs);
            var totalCarbs = (decimal)entity.Carbs;

            Bolus? correlatedBolus = null;
            if (entity.CorrelationId.HasValue &&
                bolusByCorrelationId.TryGetValue(entity.CorrelationId.Value, out var bolusEntity))
            {
                correlatedBolus = BolusMapper.ToDomainModel(bolusEntity);
            }

            var meal = new MealCarbIntake
            {
                CarbIntake = CarbIntakeMapper.ToDomainModel(entity),
                CorrelatedBolus = correlatedBolus,
                Foods = foods,
                IsAttributed = foods.Count > 0,
                AttributedCarbs = attributedCarbs,
                UnspecifiedCarbs = totalCarbs - attributedCarbs,
            };

            if (attributed.HasValue && meal.IsAttributed != attributed.Value)
                continue;

            results.Add(meal);
        }

        return Ok(results.ToArray());
    }

    #endregion

    #region Private Helpers

    private async Task<TreatmentFood?> BuildFoodEntryAsync(
        CarbIntakeFoodRequest request,
        Guid carbIntakeId,
        TreatmentFoodEntity? existing,
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
        FoodEntity? foodEntity = null;

        if (!string.IsNullOrWhiteSpace(request.FoodId))
        {
            foodEntity = Guid.TryParse(request.FoodId, out var foodGuid)
                ? await _context.Foods.AsNoTracking().FirstOrDefaultAsync(f => f.Id == foodGuid, ct)
                : await _context.Foods.AsNoTracking().FirstOrDefaultAsync(f => f.OriginalId == request.FoodId, ct);

            if (foodEntity == null)
                return null;
            foodId = foodEntity.Id;
        }
        else if (foodId.HasValue)
        {
            foodEntity = await _context.Foods.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == foodId.Value, ct);
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

        if (foodEntity == null)
            return null;

        var carbsPerPortion = (decimal)foodEntity.Carbs;

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
