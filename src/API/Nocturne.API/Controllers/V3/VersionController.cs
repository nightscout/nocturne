using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V3;

/// <summary>
/// Version controller that provides 1:1 compatibility with the Nightscout v3 version endpoint.
/// </summary>
/// <seealso cref="IVersionService"/>
/// <seealso cref="VersionResponse"/>
[ApiController]
[Tags("V3")]
[Route("api/v3/[controller]")]
public class VersionController : ControllerBase
{
    private readonly IVersionService _versionService;
    private readonly ILogger<VersionController> _logger;

    public VersionController(IVersionService versionService, ILogger<VersionController> logger)
    {
        _versionService = versionService;
        _logger = logger;
    }

    /// <summary>
    /// Get the current system version information.
    /// </summary>
    /// <returns>A <see cref="VersionResponse"/> containing version, name, server time, and git HEAD.</returns>
    /// <remarks>
    /// On error, returns a minimal <see cref="VersionResponse"/> with <c>Version = "unknown"</c>
    /// to maintain compatibility with clients that depend on this endpoint always returning 200.
    /// </remarks>
    /// <response code="200">Version information (always returns 200).</response>
    [HttpGet]
    [NightscoutEndpoint("/api/v3/version")]
    [ProducesResponseType(typeof(VersionResponse), 200)]
    public async Task<ActionResult<VersionResponse>> GetVersion()
    {
        _logger.LogDebug(
            "V3 version endpoint requested from {RemoteIpAddress}",
            HttpContext.Connection.RemoteIpAddress
        );

        try
        {
            var version = await _versionService.GetVersionAsync();

            _logger.LogDebug("Successfully returned version {Version}", version.Version);

            return Ok(version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting version information");

            // Return minimal version response even on error to maintain compatibility
            return Ok(
                new VersionResponse
                {
                    Version = "unknown",
                    Name = "Nocturne",
                    ServerTime = DateTime.UtcNow,
                    Head = "unknown",
                }
            );
        }
    }
}
