using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Step count controller for xDrip PebbleMovement step count data
/// </summary>
[ApiController]
[Route("api/v4/[controller]")]
[Tags("V4 StepCount")]
public class StepCountController : ControllerBase
{
    private readonly IStepCountService _stepCountService;
    private readonly ILogger<StepCountController> _logger;

    public StepCountController(IStepCountService stepCountService, ILogger<StepCountController> logger)
    {
        _stepCountService = stepCountService;
        _logger = logger;
    }

    /// <summary>
    /// Get step count records with optional pagination
    /// </summary>
    /// <param name="count">Maximum number of records to return (default: 10)</param>
    /// <param name="skip">Number of records to skip for pagination (default: 0)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of step count records ordered by most recent first</returns>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(IEnumerable<StepCount>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<StepCount>>> GetStepCounts(
        [FromQuery] int count = 10,
        [FromQuery] int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var records = await _stepCountService.GetStepCountsAsync(count, skip, cancellationToken);
            return Ok(records);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving step count records");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get a specific step count record by ID
    /// </summary>
    /// <param name="id">Record ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpGet("{id}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(StepCount), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<StepCount>> GetStepCount(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var record = await _stepCountService.GetStepCountByIdAsync(id, cancellationToken);
            if (record == null)
                return NotFound(new { error = $"Step count record with ID {id} not found" });

            return Ok(record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving step count record with ID {Id}", id);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Create one or more step count records (single object or array)
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(IEnumerable<StepCount>), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<StepCount>>> CreateStepCounts(
        [FromBody] object stepCounts,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (stepCounts == null)
                return BadRequest(new { error = "Step count data is required" });

            List<StepCount> stepCountList;

            if (stepCounts is System.Text.Json.JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    stepCountList =
                        System.Text.Json.JsonSerializer.Deserialize<List<StepCount>>(
                            jsonElement.GetRawText()
                        ) ?? [];
                }
                else
                {
                    var single = System.Text.Json.JsonSerializer.Deserialize<StepCount>(
                        jsonElement.GetRawText()
                    );
                    stepCountList = single != null ? [single] : [];
                }
            }
            else
            {
                return BadRequest(new { error = "Invalid data format" });
            }

            if (stepCountList.Count == 0)
                return BadRequest(new { error = "At least one step count record is required" });

            var result = await _stepCountService.CreateStepCountsAsync(stepCountList, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating step count records");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Update an existing step count record
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(StepCount), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<StepCount>> UpdateStepCount(
        string id,
        [FromBody] StepCount stepCount,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var updated = await _stepCountService.UpdateStepCountAsync(id, stepCount, cancellationToken);
            if (updated == null)
                return NotFound(new { error = $"Step count record with ID {id} not found" });

            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating step count record with ID {Id}", id);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Delete a step count record by ID
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteStepCount(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var deleted = await _stepCountService.DeleteStepCountAsync(id, cancellationToken);
            if (!deleted)
                return NotFound(new { error = $"Step count record with ID {id} not found" });

            return Ok(new { message = "Step count record deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting step count record with ID {Id}", id);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}
