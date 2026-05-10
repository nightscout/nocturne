using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4.Platform;

/// <summary>
/// Nocturne-native status controller providing detailed system status.
/// This is the V4 endpoint that returns full JSON status information.
/// For Nightscout-compatible HTML status, use /api/v1/status.
/// </summary>
/// <seealso cref="IStatusService"/>
[ApiController]
[Tags("Platform")]
[Route("api/v4/[controller]")]
[Produces("application/json")]
public class StatusController : ControllerBase
{
    private readonly IStatusService _statusService;
    private readonly ILogger<StatusController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="StatusController"/>.
    /// </summary>
    /// <param name="statusService">Service providing system status information.</param>
    /// <param name="logger">Logger instance.</param>
    public StatusController(IStatusService statusService, ILogger<StatusController> logger)
    {
        _statusService = statusService;
        _logger = logger;
    }

    /// <summary>
    /// Get detailed system status information
    /// </summary>
    /// <returns>Comprehensive system status including settings, api status, and server information</returns>
    [HttpGet]
    [ProducesResponseType(typeof(StatusResponse), 200)]
    public async Task<ActionResult<StatusResponse>> GetStatus()
    {
        _logger.LogDebug(
            "V4 Status endpoint requested from {RemoteIpAddress}",
            HttpContext.Connection.RemoteIpAddress
        );

        try
        {
            var status = await _statusService.GetSystemStatusAsync();

            _logger.LogDebug("Successfully generated V4 status response");

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating V4 status response");

            // Return minimal status response even on error to maintain compatibility
            return Ok(
                new StatusResponse
                {
                    Status = "error",
                    Name = "Nocturne",
                    Version = "unknown",
                    ServerTime = DateTime.UtcNow,
                }
            );
        }
    }

    /// <summary>
    /// Get a simple health check status
    /// </summary>
    /// <returns>Simple ok/error status</returns>
    [HttpGet("health")]
    [ProducesResponseType(typeof(object), 200)]
    public IActionResult GetHealthStatus()
    {
        return Ok(new { status = "ok", timestamp = DateTimeOffset.UtcNow });
    }
}
