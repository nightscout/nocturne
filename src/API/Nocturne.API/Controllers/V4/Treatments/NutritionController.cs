using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenApi.Remote.Attributes;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers.V4;

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
/// bolus already exists its <c>CorrelationId</c> is propagated to both records. A database transaction
/// wraps both inserts to ensure atomicity.
///
/// Demo mode is respected in <c>GET /meals</c>: when enabled only records from
/// <c>DataSources.DemoService</c> are returned; otherwise demo records are excluded.
/// </remarks>
/// <seealso cref="ICarbIntakeRepository"/>
/// <seealso cref="IBolusRepository"/>
/// <seealso cref="ITreatmentFoodService"/>
[ApiController]
[Route("api/v4/nutrition")]
[Authorize]
[Produces("application/json")]
public class NutritionController : ControllerBase
{
    private readonly ICarbIntakeRepository _carbIntakeRepo;
    private readonly IBolusRepository _bolusRepo;
    private readonly ITreatmentFoodService _treatmentFoodService;
    private readonly IDemoModeService _demoModeService;
    private readonly NocturneDbContext _context;

    public NutritionController(
        ICarbIntakeRepository carbIntakeRepo,
        IBolusRepository bolusRepo,
        ITreatmentFoodService treatmentFoodService,
        IDemoModeService demoModeService,
        NocturneDbContext context)
    {
        _carbIntakeRepo = carbIntakeRepo;
        _bolusRepo = bolusRepo;
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
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "timestamp_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
    {
        if (sort is not "timestamp_desc" and not "timestamp_asc")
            return Problem(detail: $"Invalid sort value '{sort}'. Must be 'timestamp_asc' or 'timestamp_desc'.", statusCode: 400, title: "Bad Request");
        var descending = sort == "timestamp_desc";
        var data = await _carbIntakeRepo.GetAsync(from, to, device, source, limit, offset, descending, ct: ct);
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
    [RemoteForm(Invalidates = ["GetCarbIntakes"])]
    [ProducesResponseType(typeof(CarbIntake), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CarbIntake>> CreateCarbIntake([FromBody] CreateCarbIntakeRequest request, CancellationToken ct = default)
    {
        if (request.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");

        Guid correlationId;
        if (request.CorrelationId.HasValue)
        {
            var exists = await _context.DecompositionBatches.AnyAsync(b => b.Id == request.CorrelationId.Value, ct);
            if (!exists)
            {
                _context.DecompositionBatches.Add(new DecompositionBatchEntity
                {
                    Id = request.CorrelationId.Value,
                    TenantId = _context.TenantId,
                    Source = "nutrition_controller",
                    CreatedAt = DateTime.UtcNow,
                });
                await _context.SaveChangesAsync(ct);
            }
            correlationId = request.CorrelationId.Value;
        }
        else
        {
            var batch = new DecompositionBatchEntity
            {
                TenantId = _context.TenantId,
                Source = "nutrition_controller",
                CreatedAt = DateTime.UtcNow,
            };
            _context.DecompositionBatches.Add(batch);
            await _context.SaveChangesAsync(ct);
            correlationId = batch.Id;
        }

        var model = new CarbIntake
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
            CorrelationId = correlationId,
        };

        var created = await _carbIntakeRepo.CreateAsync(model, ct);
        return CreatedAtAction(nameof(GetCarbIntakeById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing carb intake
    /// </summary>
    [HttpPut("carbs/{id:guid}")]
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
            CorrelationId = request.CorrelationId ?? existing.CorrelationId,
            LegacyId = existing.LegacyId,
            CreatedAt = existing.CreatedAt,
            AdditionalProperties = existing.AdditionalProperties,
        };

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

    /// <summary>
    /// Delete a carb intake by its external sync identifier (dataSource + syncIdentifier pair).
    /// </summary>
    [HttpDelete("carbs/by-sync-id")]
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

        var deleted = await _carbIntakeRepo.DeleteBySyncIdentifierAsync(dataSource, syncIdentifier, ct);
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
    [RemoteCommand(Invalidates = ["GetCarbIntakeFoods"])]
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
    /// Atomically create a correlated Bolus + CarbIntake for a meal event.
    /// Both records share a single CorrelationId and are persisted within a
    /// single transaction. When an existing row matches on
    /// (DataSource, SyncIdentifier), the idempotent upsert applies and the
    /// response returns 200 instead of 201.
    /// </summary>
    [HttpPost("meals")]
    [RemoteForm(Invalidates = ["GetCarbIntakes", "GetMeals"])]
    [ProducesResponseType(typeof(CreateMealResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(CreateMealResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateMealResponse>> CreateMeal(
        [FromBody] CreateMealRequest request,
        CancellationToken ct = default)
    {
        if (request.Timestamp == default)
            return Problem(detail: "Timestamp must be set", statusCode: 400, title: "Bad Request");

        Guid correlationId;
        if (request.CorrelationId.HasValue)
        {
            // Ensure a batch record exists for the supplied CorrelationId so the FK is satisfied
            var exists = await _context.DecompositionBatches.AnyAsync(b => b.Id == request.CorrelationId.Value, ct);
            if (!exists)
            {
                _context.DecompositionBatches.Add(new DecompositionBatchEntity
                {
                    Id = request.CorrelationId.Value,
                    TenantId = _context.TenantId,
                    Source = "nutrition_controller",
                    CreatedAt = DateTime.UtcNow,
                });
                await _context.SaveChangesAsync(ct);
            }
            correlationId = request.CorrelationId.Value;
        }
        else
        {
            var batch = new DecompositionBatchEntity
            {
                TenantId = _context.TenantId,
                Source = "nutrition_controller",
                CreatedAt = DateTime.UtcNow,
            };
            _context.DecompositionBatches.Add(batch);
            await _context.SaveChangesAsync(ct);
            correlationId = batch.Id;
        }
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

        await using var tx = await _context.Database.BeginTransactionAsync(ct);

        // Peek at an existing bolus with the same (DataSource, SyncIdentifier) BEFORE
        // the upsert. If one exists, its CorrelationId is authoritative and must be
        // propagated to both records (the upsert itself will overwrite it in-place).
        Guid? existingBolusCorrelationId = null;
        if (!string.IsNullOrEmpty(bolusModel.DataSource) && !string.IsNullOrEmpty(bolusModel.SyncIdentifier))
        {
            var existingEntity = await _context.Boluses
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    e => e.DataSource == bolusModel.DataSource
                      && e.SyncIdentifier == bolusModel.SyncIdentifier,
                    ct);
            existingBolusCorrelationId = existingEntity?.CorrelationId;
        }

        if (existingBolusCorrelationId.HasValue)
        {
            bolusModel.CorrelationId = existingBolusCorrelationId;
            carbModel.CorrelationId = existingBolusCorrelationId;
        }

        var bolusBefore = await _context.Boluses.CountAsync(ct);
        var createdBolus = await _bolusRepo.CreateAsync(bolusModel, ct);
        var bolusWasNew = (await _context.Boluses.CountAsync(ct)) > bolusBefore;

        var carbBefore = await _context.CarbIntakes.CountAsync(ct);
        var createdCarb = await _carbIntakeRepo.CreateAsync(carbModel, ct);
        var carbWasNew = (await _context.CarbIntakes.CountAsync(ct)) > carbBefore;

        await tx.CommitAsync(ct);

        var response = new CreateMealResponse
        {
            CorrelationId = createdBolus.CorrelationId ?? createdCarb.CorrelationId ?? correlationId,
            Bolus = createdBolus,
            CarbIntake = createdCarb,
        };

        return (bolusWasNew || carbWasNew)
            ? StatusCode(StatusCodes.Status201Created, response)
            : Ok(response);
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

        var query = _context.Set<CarbIntakeEntity>()
            .AsNoTracking()
            .Where(c => c.Timestamp >= fromTimestamp && c.Timestamp <= toTimestamp && c.Carbs > 0);

        if (_demoModeService.IsEnabled)
            query = query.Where(c => c.DataSource == DataSources.DemoService);
        else
            query = query.Where(c => c.DataSource != DataSources.DemoService);

        var carbIntakeEntities = await query
            .OrderByDescending(c => c.Timestamp)
            .ToListAsync(ct);

        if (carbIntakeEntities.Count == 0)
            return Ok(Array.Empty<MealEvent>());

        var carbIntakeIds = carbIntakeEntities.Select(c => c.Id).ToList();
        var foodEntries = await _treatmentFoodService.GetByCarbIntakeIdsAsync(carbIntakeIds, ct);
        var foodsByCarbIntake = foodEntries
            .GroupBy(f => f.CarbIntakeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var correlationIds = carbIntakeEntities
            .Where(c => c.CorrelationId.HasValue)
            .Select(c => c.CorrelationId!.Value)
            .Distinct()
            .ToList();

        var correlatedBolusEntities = correlationIds.Count > 0
            ? await _context.Set<BolusEntity>()
                .AsNoTracking()
                .Where(b => b.CorrelationId.HasValue && correlationIds.Contains(b.CorrelationId!.Value))
                .ToListAsync(ct)
            : [];

        var bolusesByCorrelation = correlatedBolusEntities
            .GroupBy(b => b.CorrelationId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(BolusMapper.ToDomainModel).ToArray());

        var events = new List<MealEvent>();

        // Pass 1: carb intakes WITH a CorrelationId — group by that key so an
        // event with multiple carb intakes (or multiple boluses) emits ONE event.
        var correlatedGroups = carbIntakeEntities
            .Where(c => c.CorrelationId.HasValue)
            .GroupBy(c => c.CorrelationId!.Value);

        foreach (var group in correlatedGroups)
        {
            var members = group.ToList();
            events.Add(BuildEvent(
                correlationId: group.Key,
                carbEntities: members,
                boluses: bolusesByCorrelation.TryGetValue(group.Key, out var bs) ? bs : [],
                foodsByCarbIntake: foodsByCarbIntake));
        }

        // Pass 2: orphan carb intakes (null CorrelationId) — each becomes its
        // own event with empty Boluses. Do NOT collapse them together.
        foreach (var orphan in carbIntakeEntities.Where(c => !c.CorrelationId.HasValue))
        {
            events.Add(BuildEvent(
                correlationId: Guid.Empty,
                carbEntities: [orphan],
                boluses: [],
                foodsByCarbIntake: foodsByCarbIntake));
        }

        var filtered = attributed.HasValue
            ? events.Where(e => e.IsAttributed == attributed.Value)
            : events;

        return Ok(filtered.OrderByDescending(e => e.Timestamp).ToArray());
    }

    private static MealEvent BuildEvent(
        Guid correlationId,
        IReadOnlyList<CarbIntakeEntity> carbEntities,
        Bolus[] boluses,
        IReadOnlyDictionary<Guid, List<TreatmentFood>> foodsByCarbIntake)
    {
        var carbModels = carbEntities.Select(CarbIntakeMapper.ToDomainModel).ToArray();
        var foods = carbEntities
            .SelectMany(c => foodsByCarbIntake.TryGetValue(c.Id, out var list) ? list : [])
            .ToArray();

        var totalCarbs = carbEntities.Sum(c => c.Carbs);
        var attributedCarbs = (double)foods.Sum(f => f.Carbs);
        var totalInsulin = boluses.Sum(b => b.Insulin);

        // The event timestamp is the earliest point across carb intakes and
        // boluses in the group — easy to reason about for chronological rendering.
        var earliestCarb = carbEntities.Min(c => c.Timestamp);
        var timestamp = boluses.Length > 0
            ? (boluses.Min(b => b.Timestamp) < earliestCarb ? boluses.Min(b => b.Timestamp) : earliestCarb)
            : earliestCarb;

        return new MealEvent
        {
            CorrelationId = correlationId,
            Timestamp = timestamp,
            CarbIntakes = carbModels,
            Boluses = boluses,
            Foods = foods,
            TotalCarbs = totalCarbs,
            AttributedCarbs = attributedCarbs,
            UnspecifiedCarbs = totalCarbs - attributedCarbs,
            TotalInsulin = totalInsulin,
            IsAttributed = foods.Length > 0,
        };
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
