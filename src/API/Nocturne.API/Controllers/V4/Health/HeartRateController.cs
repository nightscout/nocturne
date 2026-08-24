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
/// Controller for heart rate data from diabetes apps and wearables.
/// </summary>
/// <remarks>
/// Heart rate readings are stored as time-series observations. All operations delegate to
/// <see cref="IHeartRateService"/>. Callers must hold the <c>read:health</c>
/// or <c>write:health</c> scope as appropriate.
/// </remarks>
/// <seealso cref="IHeartRateService"/>
[ApiController]
[Tags("Health")]
[Route("api/v4/[controller]")]
[Produces("application/json")]
public class HeartRateController : ControllerBase
{
    /// <summary>Records returned when a caller supplies no <c>count</c> and no date range.</summary>
    private const int DefaultCount = 10;

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
    /// <param name="count">Maximum number of records to return (default: 10, or up to the ceiling when from/to are specified)</param>
    /// <param name="skip">Number of records to skip for pagination (default: 0)</param>
    /// <param name="from">Start of date range (inclusive).</param>
    /// <param name="to">End of date range (exclusive).</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of heart rate records</returns>
    /// <remarks>
    /// A date range without a <paramref name="count"/> reads up to
    /// <see cref="V4ReadLimits.MaxPageSize"/> records rather than the whole range, so a wide range
    /// cannot load the table into memory. Page through the rest with <paramref name="skip"/>.
    /// </remarks>
    [HttpGet]
    [RemoteQuery]
    [RequireScope(Scope.HeartRateRead)]
    [ProducesResponseType(typeof(IEnumerable<HeartRate>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<HeartRate>>> GetHeartRates(
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
            IEnumerable<HeartRate> records;
            if (from.HasValue && to.HasValue)
                records = await _heartRateService.GetHeartRatesByDateRangeAsync(
                    from.Value, to.Value,
                    V4ReadLimits.ClampLimit(count ?? V4ReadLimits.MaxPageSize), skip, cancellationToken);
            else
                records = await _heartRateService.GetHeartRatesAsync(
                    V4ReadLimits.ClampLimit(count ?? DefaultCount), skip, cancellationToken);

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
    [RequireScope(Scope.HeartRateRead)]
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
    [RequireScope(Scope.HeartRateReadWrite)]
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
                SyncIdentifier = request.SyncIdentifier,
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
    [RequireScope(Scope.HeartRateReadWrite)]
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
                SyncIdentifier = request.SyncIdentifier,
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
    [RequireScope(Scope.HeartRateReadWrite)]
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
