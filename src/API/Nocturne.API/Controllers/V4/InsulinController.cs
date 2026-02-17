using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for managing insulin data: boluses and bolus calculations
/// </summary>
[ApiController]
[Route("api/v4/insulin")]
[Authorize]
[Produces("application/json")]
[Tags("V4 Insulin")]
public class InsulinController : ControllerBase
{
    private readonly IBolusRepository _bolusRepo;
    private readonly IBolusCalculationRepository _bolusCalcRepo;

    public InsulinController(IBolusRepository bolusRepo, IBolusCalculationRepository bolusCalcRepo)
    {
        _bolusRepo = bolusRepo;
        _bolusCalcRepo = bolusCalcRepo;
    }

    #region Boluses

    /// <summary>
    /// Get boluses with optional filtering
    /// </summary>
    [HttpGet("boluses")]
    [RemoteQuery]
    [ProducesResponseType(typeof(PaginatedResponse<Bolus>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<Bolus>>> GetBoluses(
        [FromQuery] long? from,
        [FromQuery] long? to,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "mills_desc",
        [FromQuery] string? device = null,
        [FromQuery] string? source = null,
        CancellationToken ct = default
    )
    {
        if (sort is not "mills_desc" and not "mills_asc")
            return BadRequest(
                new { error = $"Invalid sort value '{sort}'. Must be 'mills_asc' or 'mills_desc'." }
            );
        var descending = sort == "mills_desc";
        var data = await _bolusRepo.GetAsync(
            from,
            to,
            device,
            source,
            limit,
            offset,
            descending,
            ct
        );
        var total = await _bolusRepo.CountAsync(from, to, ct);
        return Ok(
            new PaginatedResponse<Bolus> { Data = data, Pagination = new(limit, offset, total) }
        );
    }

    /// <summary>
    /// Get a bolus by ID
    /// </summary>
    [HttpGet("boluses/{id:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(Bolus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Bolus>> GetBolusById(Guid id, CancellationToken ct = default)
    {
        var result = await _bolusRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Create a new bolus
    /// </summary>
    [HttpPost("boluses")]
    [RemoteCommand(Invalidates = ["GetBoluses"])]
    [ProducesResponseType(typeof(Bolus), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Bolus>> CreateBolus(
        [FromBody] Bolus model,
        CancellationToken ct = default
    )
    {
        if (model.Mills <= 0)
            return BadRequest(new { error = "Mills must be a positive value" });
        var created = await _bolusRepo.CreateAsync(model, ct);
        return CreatedAtAction(nameof(GetBolusById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing bolus
    /// </summary>
    [HttpPut("boluses/{id:guid}")]
    [RemoteCommand(Invalidates = ["GetBoluses", "GetBolusById"])]
    [ProducesResponseType(typeof(Bolus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Bolus>> UpdateBolus(
        Guid id,
        [FromBody] Bolus model,
        CancellationToken ct = default
    )
    {
        if (model.Mills <= 0)
            return BadRequest(new { error = "Mills must be a positive value" });
        try
        {
            var updated = await _bolusRepo.UpdateAsync(id, model, ct);
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Delete a bolus
    /// </summary>
    [HttpDelete("boluses/{id:guid}")]
    [RemoteCommand(Invalidates = ["GetBoluses"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteBolus(Guid id, CancellationToken ct = default)
    {
        try
        {
            await _bolusRepo.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    #endregion

    #region Bolus Calculations

    /// <summary>
    /// Get bolus calculations with optional filtering
    /// </summary>
    [HttpGet("calculations")]
    [RemoteQuery]
    [ProducesResponseType(typeof(PaginatedResponse<BolusCalculation>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<BolusCalculation>>> GetBolusCalculations(
        [FromQuery] long? from,
        [FromQuery] long? to,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sort = "mills_desc",
        [FromQuery] string? device = null,
        [FromQuery] string? source = null,
        CancellationToken ct = default
    )
    {
        if (sort is not "mills_desc" and not "mills_asc")
            return BadRequest(
                new { error = $"Invalid sort value '{sort}'. Must be 'mills_asc' or 'mills_desc'." }
            );
        var descending = sort == "mills_desc";
        var data = await _bolusCalcRepo.GetAsync(
            from,
            to,
            device,
            source,
            limit,
            offset,
            descending,
            ct
        );
        var total = await _bolusCalcRepo.CountAsync(from, to, ct);
        return Ok(
            new PaginatedResponse<BolusCalculation>
            {
                Data = data,
                Pagination = new(limit, offset, total),
            }
        );
    }

    /// <summary>
    /// Get a bolus calculation by ID
    /// </summary>
    [HttpGet("calculations/{id:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(BolusCalculation), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BolusCalculation>> GetBolusCalculationById(
        Guid id,
        CancellationToken ct = default
    )
    {
        var result = await _bolusCalcRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Create a new bolus calculation
    /// </summary>
    [HttpPost("calculations")]
    [RemoteCommand(Invalidates = ["GetBolusCalculations"])]
    [ProducesResponseType(typeof(BolusCalculation), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BolusCalculation>> CreateBolusCalculation(
        [FromBody] BolusCalculation model,
        CancellationToken ct = default
    )
    {
        if (model.Mills <= 0)
            return BadRequest(new { error = "Mills must be a positive value" });
        var created = await _bolusCalcRepo.CreateAsync(model, ct);
        return CreatedAtAction(nameof(GetBolusCalculationById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing bolus calculation
    /// </summary>
    [HttpPut("calculations/{id:guid}")]
    [RemoteCommand(Invalidates = ["GetBolusCalculations", "GetBolusCalculationById"])]
    [ProducesResponseType(typeof(BolusCalculation), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BolusCalculation>> UpdateBolusCalculation(
        Guid id,
        [FromBody] BolusCalculation model,
        CancellationToken ct = default
    )
    {
        if (model.Mills <= 0)
            return BadRequest(new { error = "Mills must be a positive value" });
        try
        {
            var updated = await _bolusCalcRepo.UpdateAsync(id, model, ct);
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Delete a bolus calculation
    /// </summary>
    [HttpDelete("calculations/{id:guid}")]
    [RemoteCommand(Invalidates = ["GetBolusCalculations"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteBolusCalculation(Guid id, CancellationToken ct = default)
    {
        try
        {
            await _bolusCalcRepo.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    #endregion
}
