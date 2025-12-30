using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for treatment food breakdown and meals workflow.
/// </summary>
[ApiController]
[Route("api/v4/treatments")]
[Tags("V4 Treatment Foods")]
public class TreatmentFoodsController : ControllerBase
{
    private readonly NocturneDbContext _context;
    private readonly ITreatmentFoodService _treatmentFoodService;
    private readonly IDemoModeService _demoModeService;
    private readonly ILogger<TreatmentFoodsController> _logger;

    public TreatmentFoodsController(
        NocturneDbContext context,
        ITreatmentFoodService treatmentFoodService,
        IDemoModeService demoModeService,
        ILogger<TreatmentFoodsController> logger
    )
    {
        _context = context;
        _treatmentFoodService = treatmentFoodService;
        _demoModeService = demoModeService;
        _logger = logger;
    }

    /// <summary>
    /// Get food breakdown for a treatment.
    /// </summary>
    [HttpGet("{id}/foods")]
    [Authorize]
    public async Task<ActionResult<TreatmentFoodBreakdown>> GetTreatmentFoods(string id)
    {
        var treatment = await ResolveTreatmentEntityAsync(id, HttpContext.RequestAborted);
        if (treatment == null)
        {
            return NotFound();
        }

        var breakdown = await _treatmentFoodService.GetByTreatmentIdAsync(
            treatment.Id,
            HttpContext.RequestAborted
        );

        if (breakdown == null)
        {
            return NotFound();
        }

        return Ok(breakdown);
    }

    /// <summary>
    /// Add a food breakdown entry to a treatment.
    /// </summary>
    [HttpPost("{id}/foods")]
    [Authorize]
    public async Task<ActionResult<TreatmentFoodBreakdown>> AddTreatmentFood(
        string id,
        [FromBody] TreatmentFoodRequest request
    )
    {
        var treatment = await ResolveTreatmentEntityAsync(id, HttpContext.RequestAborted);
        if (treatment == null)
        {
            return NotFound();
        }

        var entry = await BuildEntryAsync(request, treatment, null, HttpContext.RequestAborted);
        if (entry == null)
        {
            return BadRequest();
        }

        await _treatmentFoodService.AddAsync(entry, HttpContext.RequestAborted);

        var breakdown = await _treatmentFoodService.GetByTreatmentIdAsync(
            treatment.Id,
            HttpContext.RequestAborted
        );

        return Ok(breakdown);
    }

    /// <summary>
    /// Update a food breakdown entry.
    /// </summary>
    [HttpPut("{id}/foods/{foodEntryId:guid}")]
    [Authorize]
    public async Task<ActionResult<TreatmentFoodBreakdown>> UpdateTreatmentFood(
        string id,
        Guid foodEntryId,
        [FromBody] TreatmentFoodRequest request
    )
    {
        var treatment = await ResolveTreatmentEntityAsync(id, HttpContext.RequestAborted);
        if (treatment == null)
        {
            return NotFound();
        }

        var existing = await _context
            .Set<TreatmentFoodEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(tf => tf.Id == foodEntryId, HttpContext.RequestAborted);

        if (existing == null || existing.TreatmentId != treatment.Id)
        {
            return NotFound();
        }

        var entry = await BuildEntryAsync(request, treatment, existing, HttpContext.RequestAborted);
        if (entry == null)
        {
            return BadRequest();
        }

        entry.Id = foodEntryId;

        var updated = await _treatmentFoodService.UpdateAsync(entry, HttpContext.RequestAborted);
        if (updated == null)
        {
            return NotFound();
        }

        var breakdown = await _treatmentFoodService.GetByTreatmentIdAsync(
            treatment.Id,
            HttpContext.RequestAborted
        );

        return Ok(breakdown);
    }

    /// <summary>
    /// Remove a food breakdown entry.
    /// </summary>
    [HttpDelete("{id}/foods/{foodEntryId:guid}")]
    [Authorize]
    public async Task<ActionResult<TreatmentFoodBreakdown>> DeleteTreatmentFood(
        string id,
        Guid foodEntryId
    )
    {
        var treatment = await ResolveTreatmentEntityAsync(id, HttpContext.RequestAborted);
        if (treatment == null)
        {
            return NotFound();
        }

        var existing = await _context
            .Set<TreatmentFoodEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(tf => tf.Id == foodEntryId, HttpContext.RequestAborted);

        if (existing == null || existing.TreatmentId != treatment.Id)
        {
            return NotFound();
        }

        await _treatmentFoodService.DeleteAsync(foodEntryId, HttpContext.RequestAborted);

        var breakdown = await _treatmentFoodService.GetByTreatmentIdAsync(
            treatment.Id,
            HttpContext.RequestAborted
        );

        return Ok(breakdown);
    }

    /// <summary>
    /// Get carb treatments with attribution status for meals view.
    /// </summary>
    [HttpGet("meals")]
    [Authorize]
    public async Task<ActionResult<MealTreatment[]>> GetMeals(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] bool? attributed = null
    )
    {
        var fromDate = from ?? DateTime.UtcNow.Date;
        var toDate = to ?? DateTime.UtcNow.Date.AddDays(1);

        var fromMills = new DateTimeOffset(fromDate).ToUnixTimeMilliseconds();
        var toMills = new DateTimeOffset(toDate).ToUnixTimeMilliseconds();

        var query = _context
            .Treatments.AsNoTracking()
            .Where(t =>
                t.Mills >= fromMills && t.Mills <= toMills && t.Carbs.HasValue && t.Carbs > 0
            );

        if (_demoModeService.IsEnabled)
        {
            query = query.Where(t => t.DataSource == DataSources.DemoService);
        }
        else
        {
            query = query.Where(t => t.DataSource != DataSources.DemoService);
        }

        var treatmentEntities = await query
            .OrderByDescending(t => t.Mills)
            .ToListAsync(HttpContext.RequestAborted);

        if (treatmentEntities.Count == 0)
        {
            return Ok(Array.Empty<MealTreatment>());
        }

        var treatmentIds = treatmentEntities.Select(t => t.Id).ToList();
        var breakdownEntries = await _treatmentFoodService.GetByTreatmentIdsAsync(
            treatmentIds,
            HttpContext.RequestAborted
        );

        var groupedEntries = breakdownEntries
            .GroupBy(entry => entry.TreatmentId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var results = new List<MealTreatment>();

        foreach (var treatmentEntity in treatmentEntities)
        {
            var entries = groupedEntries.TryGetValue(treatmentEntity.Id, out var list)
                ? list
                : new List<TreatmentFood>();
            var attributedCarbs = entries.Sum(entry => entry.Carbs);
            var totalCarbs = treatmentEntity.Carbs.HasValue
                ? (decimal)treatmentEntity.Carbs.Value
                : 0m;

            var meal = new MealTreatment
            {
                Treatment = TreatmentMapper.ToDomainModel(treatmentEntity),
                Foods = entries,
                IsAttributed = entries.Count > 0,
                AttributedCarbs = attributedCarbs,
                UnspecifiedCarbs = totalCarbs - attributedCarbs,
            };

            if (attributed.HasValue && meal.IsAttributed != attributed.Value)
            {
                continue;
            }

            results.Add(meal);
        }

        return Ok(results.ToArray());
    }

    private async Task<TreatmentEntity?> ResolveTreatmentEntityAsync(
        string id,
        CancellationToken cancellationToken
    )
    {
        var entity = await _context
            .Treatments.AsNoTracking()
            .FirstOrDefaultAsync(t => t.OriginalId == id, cancellationToken);

        if (entity != null)
        {
            return entity;
        }

        return Guid.TryParse(id, out var guid)
            ? await _context
                .Treatments.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == guid, cancellationToken)
            : null;
    }

    private async Task<FoodEntity?> ResolveFoodEntityAsync(
        string id,
        CancellationToken cancellationToken
    )
    {
        var entity = await _context
            .Foods.AsNoTracking()
            .FirstOrDefaultAsync(f => f.OriginalId == id, cancellationToken);

        if (entity != null)
        {
            return entity;
        }

        return Guid.TryParse(id, out var guid)
            ? await _context
                .Foods.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == guid, cancellationToken)
            : null;
    }

    private async Task<TreatmentFood?> BuildEntryAsync(
        TreatmentFoodRequest request,
        TreatmentEntity treatment,
        TreatmentFoodEntity? existing,
        CancellationToken cancellationToken
    )
    {
        var timeOffset = request.TimeOffsetMinutes ?? existing?.TimeOffsetMinutes ?? 0;
        var note = request.Note ?? existing?.Note;

        if (
            existing != null
            && request.FoodId == null
            && !request.Carbs.HasValue
            && !request.Portions.HasValue
            && !request.InputMode.HasValue
        )
        {
            return new TreatmentFood
            {
                Id = existing.Id,
                TreatmentId = treatment.Id,
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
            foodEntity = await ResolveFoodEntityAsync(request.FoodId, cancellationToken);
            if (foodEntity == null)
            {
                return null;
            }
            foodId = foodEntity.Id;
        }
        else if (foodId.HasValue)
        {
            foodEntity = await _context
                .Foods.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == foodId.Value, cancellationToken);
        }

        var portions = request.Portions ?? existing?.Portions ?? 0m;
        var carbs = request.Carbs ?? existing?.Carbs ?? 0m;

        var inputMode = request.InputMode;
        if (inputMode == null)
        {
            inputMode =
                request.Carbs.HasValue && !request.Portions.HasValue
                    ? TreatmentFoodInputMode.Carbs
                    : TreatmentFoodInputMode.Portions;
        }

        if (!foodId.HasValue)
        {
            if (carbs <= 0m)
            {
                return null;
            }

            return new TreatmentFood
            {
                TreatmentId = treatment.Id,
                FoodId = null,
                Portions = 0m,
                Carbs = carbs,
                TimeOffsetMinutes = timeOffset,
                Note = note,
            };
        }

        if (foodEntity == null)
        {
            return null;
        }

        var carbsPerPortion = (decimal)foodEntity.Carbs;

        if (inputMode == TreatmentFoodInputMode.Portions)
        {
            if (portions <= 0m)
            {
                return null;
            }

            carbs = Math.Round(carbsPerPortion * portions, 1, MidpointRounding.AwayFromZero);
        }
        else
        {
            if (carbs < 0m || carbsPerPortion <= 0m)
            {
                return null;
            }

            portions = Math.Round(carbs / carbsPerPortion, 2, MidpointRounding.AwayFromZero);

            if (portions <= 0m)
            {
                return null;
            }
        }

        return new TreatmentFood
        {
            TreatmentId = treatment.Id,
            FoodId = foodId,
            Portions = portions,
            Carbs = carbs,
            TimeOffsetMinutes = timeOffset,
            Note = note,
        };
    }
}

public class TreatmentFoodRequest
{
    public string? FoodId { get; set; }
    public decimal? Portions { get; set; }
    public decimal? Carbs { get; set; }
    public int? TimeOffsetMinutes { get; set; }
    public string? Note { get; set; }
    public TreatmentFoodInputMode? InputMode { get; set; }
}

public enum TreatmentFoodInputMode
{
    Portions,
    Carbs,
}
