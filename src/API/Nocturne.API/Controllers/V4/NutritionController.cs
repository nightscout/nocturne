using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for managing nutrition data: carbohydrate intakes
/// </summary>
[ApiController]
[Route("api/v4/nutrition")]
[Authorize]
[Produces("application/json")]
[Tags("V4 Nutrition")]
public class NutritionController : ControllerBase
{
    private readonly CarbIntakeRepository _carbIntakeRepo;

    public NutritionController(CarbIntakeRepository carbIntakeRepo)
    {
        _carbIntakeRepo = carbIntakeRepo;
    }

    #region Carb Intakes

    /// <summary>
    /// Get carb intakes with optional filtering
    /// </summary>
    [HttpGet("carbs")]
    [ProducesResponseType(typeof(PaginatedResponse<CarbIntake>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaginatedResponse<CarbIntake>>> GetCarbIntakes(
        [FromQuery] long? from, [FromQuery] long? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "mills_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
    {
        var descending = sort == "mills_desc";
        var data = await _carbIntakeRepo.GetAsync(from, to, device, source, limit, offset, descending, ct);
        var total = await _carbIntakeRepo.CountAsync(from, to, ct);
        return Ok(new PaginatedResponse<CarbIntake> { Data = data, Pagination = new(limit, offset, total) });
    }

    /// <summary>
    /// Get a carb intake by ID
    /// </summary>
    [HttpGet("carbs/{id:guid}")]
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
    [ProducesResponseType(typeof(CarbIntake), StatusCodes.Status201Created)]
    public async Task<ActionResult<CarbIntake>> CreateCarbIntake([FromBody] CarbIntake model, CancellationToken ct = default)
    {
        var created = await _carbIntakeRepo.CreateAsync(model, ct);
        return CreatedAtAction(nameof(GetCarbIntakeById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing carb intake
    /// </summary>
    [HttpPut("carbs/{id:guid}")]
    [ProducesResponseType(typeof(CarbIntake), StatusCodes.Status200OK)]
    public async Task<ActionResult<CarbIntake>> UpdateCarbIntake(Guid id, [FromBody] CarbIntake model, CancellationToken ct = default)
    {
        var updated = await _carbIntakeRepo.UpdateAsync(id, model, ct);
        return Ok(updated);
    }

    /// <summary>
    /// Delete a carb intake
    /// </summary>
    [HttpDelete("carbs/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteCarbIntake(Guid id, CancellationToken ct = default)
    {
        await _carbIntakeRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    #endregion
}
