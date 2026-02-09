using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V2;

/// <summary>
/// V2 DData controller providing direct data access endpoints
/// Implements the legacy /api/v2/ddata endpoints with 1:1 backwards compatibility
/// </summary>
[ApiController]
[Route("api/v2/ddata")]
[Produces("application/json")]
[Tags("V2 DData")]
[ClientPropertyName("v2DData")]
public class DDataController : ControllerBase
{
    private readonly IDDataService _ddataService;
    private readonly ILogger<DDataController> _logger;

    public DDataController(IDDataService ddataService, ILogger<DDataController> logger)
    {
        _ddataService = ddataService;
        _logger = logger;
    }

    /// <summary>
    /// Get current DData structure
    /// Returns comprehensive data structure containing SGVs, treatments, profiles, and device status
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Complete DData structure for current time</returns>
    /// <response code="200">Returns the DData structure</response>
    /// <response code="500">If there was an internal server error</response>
    [HttpGet]
    [ProducesResponseType(typeof(DDataResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<DDataResponse>> GetDData(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var result = await _ddataService.GetDDataWithRecentStatusesAsync(
                currentTime,
                cancellationToken
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current DData");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get DData structure for a specific timestamp
    /// Returns data relevant to the specified timestamp
    /// </summary>
    /// <param name="timestamp">Unix timestamp in milliseconds or ISO date string</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>DData structure for the specified timestamp</returns>
    /// <response code="200">Returns the DData structure</response>
    /// <response code="400">If the timestamp parameter is invalid</response>
    /// <response code="500">If there was an internal server error</response>
    [HttpGet("at/{timestamp}")]
    [ProducesResponseType(typeof(DDataResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<DDataResponse>> GetDDataAt(
        string timestamp,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            long unixTimestamp;

            // Try to parse as Unix timestamp (milliseconds)
            if (long.TryParse(timestamp, out unixTimestamp))
            {
                // Validate reasonable timestamp range
                if (
                    unixTimestamp < 0
                    || unixTimestamp
                        > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                            + (365L * 24 * 60 * 60 * 1000)
                )
                {
                    return BadRequest(new { error = "Timestamp out of valid range" });
                }
            }
            // Try to parse as ISO date string
            else if (
                DateTime.TryParse(
                    timestamp,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsedDate
                )
            )
            {
                unixTimestamp = (
                    (DateTimeOffset)DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc)
                ).ToUnixTimeMilliseconds();
            }
            else
            {
                return BadRequest(
                    new
                    {
                        error = "Invalid timestamp format. Use Unix milliseconds or ISO date string.",
                    }
                );
            }

            var result = await _ddataService.GetDDataWithRecentStatusesAsync(
                unixTimestamp,
                cancellationToken
            );
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting DData for timestamp {Timestamp}", timestamp);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get raw DData structure (without recent status filtering)
    /// Returns the complete DData structure without filtering recent device statuses
    /// </summary>
    /// <param name="timestamp">Optional Unix timestamp in milliseconds or ISO date string</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Raw DData structure</returns>
    /// <response code="200">Returns the raw DData structure</response>
    /// <response code="400">If the timestamp parameter is invalid</response>
    /// <response code="500">If there was an internal server error</response>
    [HttpGet("raw")]
    [ProducesResponseType(typeof(DData), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<DData>> GetRawDData(
        [FromQuery] string? timestamp = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            long unixTimestamp;

            if (string.IsNullOrEmpty(timestamp))
            {
                unixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            else if (long.TryParse(timestamp, out unixTimestamp))
            {
                // Validate reasonable timestamp range
                if (
                    unixTimestamp < 0
                    || unixTimestamp
                        > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                            + (365L * 24 * 60 * 60 * 1000)
                )
                {
                    return BadRequest(new { error = "Timestamp out of valid range" });
                }
            }
            else if (
                DateTime.TryParse(
                    timestamp,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsedDate
                )
            )
            {
                unixTimestamp = (
                    (DateTimeOffset)DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc)
                ).ToUnixTimeMilliseconds();
            }
            else
            {
                return BadRequest(
                    new
                    {
                        error = "Invalid timestamp format. Use Unix milliseconds or ISO date string.",
                    }
                );
            }

            var result = await _ddataService.GetDDataAsync(unixTimestamp, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting raw DData for timestamp {Timestamp}", timestamp);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}
