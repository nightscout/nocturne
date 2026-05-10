using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4.Health;

/// <summary>
/// Controller for body weight tracking data.
/// </summary>
/// <remarks>
/// Provides time-series weight readings sourced from connected health apps.
/// All read and write operations delegate to <see cref="IBodyWeightService"/>.
/// </remarks>
/// <seealso cref="IBodyWeightService"/>
[ApiController]
[Tags("Health")]
[Route("api/v4/body-weight")]
public class BodyWeightController : ControllerBase
{
    private readonly IBodyWeightService _bodyWeightService;
    private readonly ILogger<BodyWeightController> _logger;

    public BodyWeightController(IBodyWeightService bodyWeightService, ILogger<BodyWeightController> logger)
    {
        _bodyWeightService = bodyWeightService;
        _logger = logger;
    }

    /// <summary>
    /// Get body weight records with optional pagination
    /// </summary>
    /// <param name="count">Maximum number of records to return (default: 10)</param>
    /// <param name="skip">Number of records to skip for pagination (default: 0)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of body weight records ordered by most recent first</returns>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(IEnumerable<BodyWeight>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<BodyWeight>>> GetBodyWeights(
        [FromQuery] int count = 10,
        [FromQuery] int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var records = await _bodyWeightService.GetBodyWeightsAsync(count, skip, cancellationToken);
            return Ok(records);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving body weight records");
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Get a specific body weight record by ID
    /// </summary>
    /// <param name="id">Record ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpGet("{id}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(BodyWeight), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<BodyWeight>> GetBodyWeight(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var record = await _bodyWeightService.GetBodyWeightByIdAsync(id, cancellationToken);
            if (record == null)
                return Problem(detail: $"Body weight record with ID {id} not found", statusCode: 404, title: "Not Found");

            return Ok(record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving body weight record with ID {Id}", id);
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Create a single body weight record
    /// </summary>
    [HttpPost]
    [RemoteCommand(Invalidates = ["GetBodyWeights"])]
    [ProducesResponseType(typeof(BodyWeight), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<BodyWeight>> Create(
        [FromBody] BodyWeight bodyWeight,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (bodyWeight == null)
                return Problem(detail: "Body weight data is required", statusCode: 400, title: "Bad Request");

            var result = await _bodyWeightService.CreateBodyWeightsAsync([bodyWeight], cancellationToken);
            return StatusCode(StatusCodes.Status201Created, result.First());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating body weight record");
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Create one or more body weight records (single object or array)
    /// </summary>
    [HttpPost("batch")]
    [ProducesResponseType(typeof(IEnumerable<BodyWeight>), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<BodyWeight>>> CreateBodyWeights(
        [FromBody] object bodyWeights,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (bodyWeights == null)
                return Problem(detail: "Body weight data is required", statusCode: 400, title: "Bad Request");

            List<BodyWeight> bodyWeightList;

            if (bodyWeights is System.Text.Json.JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    bodyWeightList =
                        System.Text.Json.JsonSerializer.Deserialize<List<BodyWeight>>(
                            jsonElement.GetRawText()
                        ) ?? [];
                }
                else
                {
                    var single = System.Text.Json.JsonSerializer.Deserialize<BodyWeight>(
                        jsonElement.GetRawText()
                    );
                    bodyWeightList = single != null ? [single] : [];
                }
            }
            else
            {
                return Problem(detail: "Invalid data format", statusCode: 400, title: "Bad Request");
            }

            if (bodyWeightList.Count == 0)
                return Problem(detail: "At least one body weight record is required", statusCode: 400, title: "Bad Request");

            var result = await _bodyWeightService.CreateBodyWeightsAsync(bodyWeightList, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating body weight records");
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Update an existing body weight record
    /// </summary>
    [HttpPut("{id}")]
    [RemoteCommand(Invalidates = ["GetBodyWeights", "GetBodyWeight"])]
    [ProducesResponseType(typeof(BodyWeight), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<BodyWeight>> UpdateBodyWeight(
        string id,
        [FromBody] BodyWeight bodyWeight,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var updated = await _bodyWeightService.UpdateBodyWeightAsync(id, bodyWeight, cancellationToken);
            if (updated == null)
                return Problem(detail: $"Body weight record with ID {id} not found", statusCode: 404, title: "Not Found");

            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating body weight record with ID {Id}", id);
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Delete a body weight record by ID
    /// </summary>
    [HttpDelete("{id}")]
    [RemoteCommand(Invalidates = ["GetBodyWeights", "GetBodyWeight"])]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteBodyWeight(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var deleted = await _bodyWeightService.DeleteBodyWeightAsync(id, cancellationToken);
            if (!deleted)
                return Problem(detail: $"Body weight record with ID {id} not found", statusCode: 404, title: "Not Found");

            return Ok(new { message = "Body weight record deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting body weight record with ID {Id}", id);
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }
}
