using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V3;

/// <summary>
/// V3 Status controller that provides extended status information with permissions and authorization.
/// Returns system status in the Nightscout V3 format including authorization details, permissions,
/// available collections, and supported API versions.
/// </summary>
/// <seealso cref="IStatusService"/>
/// <seealso cref="V3StatusResponse"/>
[ApiController]
[Tags("V3")]
[Route("api/v3/[controller]")]
public class StatusController : ControllerBase
{
    private readonly IStatusService _statusService;
    private readonly ILogger<StatusController> _logger;

    public StatusController(IStatusService statusService, ILogger<StatusController> logger)
    {
        _statusService = statusService;
        _logger = logger;
    }

    /// <summary>
    /// Get the current system status with extended V3 information.
    /// </summary>
    /// <returns>A <see cref="V3StatusResponse"/> containing authorization, permissions, and API version details.</returns>
    /// <remarks>
    /// On error, returns a minimal <see cref="V3StatusResponse"/> with <c>Status = "error"</c> to maintain
    /// compatibility with clients that depend on this endpoint always returning 200.
    /// </remarks>
    /// <response code="200">System status (always returns 200, even on internal errors).</response>
    [HttpGet]
    [NightscoutEndpoint("/api/v3/status")]
    [ProducesResponseType(typeof(V3StatusResponse), 200)]
    public async Task<ActionResult<V3StatusResponse>> GetStatus()
    {
        _logger.LogDebug(
            "V3 status endpoint requested from {RemoteIpAddress}",
            HttpContext.Connection.RemoteIpAddress
        );

        try
        {
            var status = await _statusService.GetV3SystemStatusAsync();

            _logger.LogDebug("Successfully generated V3 status response");

            return Ok(new { status = 200, result = status });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating V3 status response");

            // Return minimal status response even on error to maintain compatibility
            return Ok(new
            {
                status = 200,
                result = new V3StatusResponse
                {
                    Status = "error",
                    Name = "Nocturne",
                    Version = "unknown",
                    ServerTime = DateTime.UtcNow,
                    Extended = new ExtendedStatusInfo
                    {
                        Authorization = new AuthorizationInfo
                        {
                            IsAuthorized = false,
                            Scope = new List<string>(),
                            Roles = new List<string>(),
                        },
                        Permissions = new Dictionary<string, bool>(),
                        Collections = new List<string>(),
                        ApiVersions = new Dictionary<string, bool> { ["v3"] = true },
                    },
                },
            });
        }
    }
}
