using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;
using OpenApi.Remote.Attributes;

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
[AllowAnonymous]
public class StatusController : ControllerBase
{
    private readonly IStatusService _statusService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ILogger<StatusController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="StatusController"/>.
    /// </summary>
    /// <param name="statusService">Service providing system status information.</param>
    /// <param name="tenantAccessor">Accessor for the tenant this request resolved to, if any.</param>
    /// <param name="logger">Logger instance.</param>
    public StatusController(
        IStatusService statusService,
        ITenantAccessor tenantAccessor,
        ILogger<StatusController> logger)
    {
        _statusService = statusService;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Get detailed system status information
    /// </summary>
    /// <returns>Comprehensive system status including settings, api status, and server information</returns>
    [HttpGet]
    [RemoteQuery]
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

            // Stamped here rather than in the service: the service caches its response per
            // tenant, and this must also be right on the error path below, where no service
            // response exists at all — which is exactly the apex-with-several-tenants case
            // the web app reads this field to detect.
            status.TenantSlug = _tenantAccessor.Context?.Slug;

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
                    TenantSlug = _tenantAccessor.Context?.Slug,
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
