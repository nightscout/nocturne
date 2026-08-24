using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Health;

/// <summary>
/// Controller for step count data from diabetes apps and wearables.
/// </summary>
/// <remarks>
/// Step count readings are stored as time-series observations.
/// All operations delegate to <see cref="IStepCountService"/>. Callers must hold the
/// appropriate scope (<c>read:health</c> or <c>write:health</c>).
/// </remarks>
/// <seealso cref="IStepCountService"/>
[ApiController]
[Tags("Health")]
[Route("api/v4/[controller]")]
[Produces("application/json")]
public class StepCountController : ControllerBase
{
    /// <summary>Records returned when a caller supplies no <c>count</c> and no date range.</summary>
    private const int DefaultCount = 10;

    private readonly IStepCountService _stepCountService;
    private readonly ILogger<StepCountController> _logger;

    public StepCountController(IStepCountService stepCountService, ILogger<StepCountController> logger)
    {
        _stepCountService = stepCountService;
        _logger = logger;
    }

    /// <summary>
    /// Get step count records with optional pagination and date filtering
    /// </summary>
    /// <param name="count">Maximum number of records to return (default: 10, or up to the ceiling when from/to are specified)</param>
    /// <param name="skip">Number of records to skip for pagination (default: 0)</param>
    /// <param name="from">Start of date range (inclusive).</param>
    /// <param name="to">End of date range (exclusive).</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of step count records</returns>
    /// <remarks>
    /// A date range without a <paramref name="count"/> reads up to
    /// <see cref="V4ReadLimits.MaxPageSize"/> records rather than the whole range, so a wide range
    /// cannot load the table into memory. Page through the rest with <paramref name="skip"/>.
    /// </remarks>
    [HttpGet]
    [RemoteQuery]
    [RequireScope(Scope.StepCountRead)]
    [ProducesResponseType(typeof(IEnumerable<StepCount>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<StepCount>>> GetStepCounts(
        [FromQuery] int? count = null,
        [FromQuery] int skip = 0,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken cancellationToken = default
    )
    {
        skip = V4ReadLimits.ClampOffset(skip);

        try
        {
            IEnumerable<StepCount> records;
            if (from.HasValue && to.HasValue)
                records = await _stepCountService.GetStepCountsByDateRangeAsync(
                    from.Value, to.Value,
                    V4ReadLimits.ClampLimit(count ?? V4ReadLimits.MaxPageSize), skip, cancellationToken);
            else
                records = await _stepCountService.GetStepCountsAsync(
                    V4ReadLimits.ClampLimit(count ?? DefaultCount), skip, cancellationToken);

            return Ok(records);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving step count records");
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Get a specific step count record by ID
    /// </summary>
    /// <param name="id">Record ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpGet("{id}")]
    [RemoteQuery]
    [RequireScope(Scope.StepCountRead)]
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
                return Problem(detail: $"Step count record with ID {id} not found", statusCode: 404, title: "Not Found");

            return Ok(record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving step count record with ID {Id}", id);
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Create one or more step count records
    /// </summary>
    [HttpPost]
    [RequireScope(Scope.StepCountReadWrite)]
    [ProducesResponseType(typeof(IEnumerable<StepCount>), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<StepCount>>> CreateStepCounts(
        [FromBody] UpsertStepCountRequest[] requests,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (requests.Length == 0)
                return Problem(detail: "At least one step count record is required", statusCode: 400, title: "Bad Request");

            var stepCountList = requests.Select(request => new StepCount
            {
                Timestamp = request.Timestamp.UtcDateTime,
                UtcOffset = request.UtcOffset,
                Metric = request.Metric,
                Source = request.Source,
                Device = request.Device,
                EnteredBy = request.App,
                DataSource = request.DataSource,
                SyncIdentifier = request.SyncIdentifier,
            }).ToList();

            var result = await _stepCountService.CreateStepCountsAsync(stepCountList, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating step count records");
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Update an existing step count record
    /// </summary>
    [HttpPut("{id}")]
    [RequireScope(Scope.StepCountReadWrite)]
    [ProducesResponseType(typeof(StepCount), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<StepCount>> UpdateStepCount(
        string id,
        [FromBody] UpsertStepCountRequest request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var stepCount = new StepCount
            {
                Timestamp = request.Timestamp.UtcDateTime,
                UtcOffset = request.UtcOffset,
                Metric = request.Metric,
                Source = request.Source,
                Device = request.Device,
                EnteredBy = request.App,
                DataSource = request.DataSource,
                SyncIdentifier = request.SyncIdentifier,
            };

            var updated = await _stepCountService.UpdateStepCountAsync(id, stepCount, cancellationToken);
            if (updated == null)
                return Problem(detail: $"Step count record with ID {id} not found", statusCode: 404, title: "Not Found");

            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating step count record with ID {Id}", id);
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Delete a step count record by ID
    /// </summary>
    [HttpDelete("{id}")]
    [RequireScope(Scope.StepCountReadWrite)]
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
                return Problem(detail: $"Step count record with ID {id} not found", statusCode: 404, title: "Not Found");

            return Ok(new { message = "Step count record deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting step count record with ID {Id}", id);
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }
}
