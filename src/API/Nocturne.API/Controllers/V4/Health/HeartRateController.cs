using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Health;

/// <summary>
/// Controller for heart rate data from diabetes apps and wearables.
/// </summary>
/// <remarks>
/// Heart rate readings are stored as time-series observations. All operations delegate to
/// <see cref="IHeartRateService"/>. Callers must hold the <c>read:health</c>
/// or <c>write:health</c> scope as appropriate.
/// </remarks>
/// <seealso cref="IHeartRateService"/>
[ApiController]
[Route("api/v4/[controller]")]
[Authorize]
[Produces("application/json")]
public class HeartRateController : ControllerBase
{
    private readonly IHeartRateService _heartRateService;
    private readonly ILogger<HeartRateController> _logger;

    public HeartRateController(IHeartRateService heartRateService, ILogger<HeartRateController> logger)
    {
        _heartRateService = heartRateService;
        _logger = logger;
    }

    /// <summary>
    /// Get heart rate records with optional pagination and date filtering
    /// </summary>
    /// <param name="count">Maximum number of records to return (default: 10, ignored when from/to are specified)</param>
    /// <param name="skip">Number of records to skip for pagination (default: 0, ignored when from/to are specified)</param>
    /// <param name="from">Start of date range (inclusive). When specified with 'to', returns all records in range.</param>
    /// <param name="to">End of date range (exclusive). When specified with 'from', returns all records in range.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of heart rate records</returns>
    [HttpGet]
    [RemoteQuery]
    [RequireScope(OAuthScopes.HeartRateRead)]
    [ProducesResponseType(typeof(IEnumerable<HeartRate>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<HeartRate>>> GetHeartRates(
        [FromQuery] int count = 10,
        [FromQuery] int skip = 0,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            IEnumerable<HeartRate> records;
            if (from.HasValue && to.HasValue)
                records = await _heartRateService.GetHeartRatesByDateRangeAsync(from.Value, to.Value, cancellationToken);
            else
                records = await _heartRateService.GetHeartRatesAsync(count, skip, cancellationToken);

            return Ok(records);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving heart rate records");
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Get a specific heart rate record by ID
    /// </summary>
    /// <param name="id">Record ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpGet("{id}")]
    [RemoteQuery]
    [RequireScope(OAuthScopes.HeartRateRead)]
    [ProducesResponseType(typeof(HeartRate), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<HeartRate>> GetHeartRate(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var record = await _heartRateService.GetHeartRateByIdAsync(id, cancellationToken);
            if (record == null)
                return Problem(detail: $"Heart rate record with ID {id} not found", statusCode: 404, title: "Not Found");

            return Ok(record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving heart rate record with ID {Id}", id);
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Create one or more heart rate records
    /// </summary>
    [HttpPost]
    [RequireScope(OAuthScopes.HeartRateReadWrite)]
    [ProducesResponseType(typeof(IEnumerable<HeartRate>), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<HeartRate>>> CreateHeartRates(
        [FromBody] UpsertHeartRateRequest[] requests,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (requests.Length == 0)
                return Problem(detail: "At least one heart rate record is required", statusCode: 400, title: "Bad Request");

            var heartRateList = requests.Select(request => new HeartRate
            {
                Timestamp = request.Timestamp.UtcDateTime,
                UtcOffset = request.UtcOffset,
                Bpm = request.Bpm,
                Accuracy = request.Accuracy,
                Device = request.Device,
                EnteredBy = request.App,
                DataSource = request.DataSource,
            }).ToList();

            var result = await _heartRateService.CreateHeartRatesAsync(heartRateList, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating heart rate records");
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Update an existing heart rate record
    /// </summary>
    [HttpPut("{id}")]
    [RequireScope(OAuthScopes.HeartRateReadWrite)]
    [ProducesResponseType(typeof(HeartRate), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<HeartRate>> UpdateHeartRate(
        string id,
        [FromBody] UpsertHeartRateRequest request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var heartRate = new HeartRate
            {
                Timestamp = request.Timestamp.UtcDateTime,
                UtcOffset = request.UtcOffset,
                Bpm = request.Bpm,
                Accuracy = request.Accuracy,
                Device = request.Device,
                EnteredBy = request.App,
                DataSource = request.DataSource,
            };

            var updated = await _heartRateService.UpdateHeartRateAsync(id, heartRate, cancellationToken);
            if (updated == null)
                return Problem(detail: $"Heart rate record with ID {id} not found", statusCode: 404, title: "Not Found");

            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating heart rate record with ID {Id}", id);
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Delete a heart rate record by ID
    /// </summary>
    [HttpDelete("{id}")]
    [RequireScope(OAuthScopes.HeartRateReadWrite)]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult> DeleteHeartRate(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var deleted = await _heartRateService.DeleteHeartRateAsync(id, cancellationToken);
            if (!deleted)
                return Problem(detail: $"Heart rate record with ID {id} not found", statusCode: 404, title: "Not Found");

            return Ok(new { message = "Heart rate record deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting heart rate record with ID {Id}", id);
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }
}
