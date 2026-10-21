using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V3;

/// <summary>
/// Version controller that provides 1:1 compatibility with the Nightscout v3 version endpoint.
/// </summary>
/// <seealso cref="IStatusService"/>
/// <seealso cref="V3VersionResult"/>
[ApiController]
[Tags("V3 Version")]
[Route("api/v3/[controller]")]
[AllowAnonymous]
public class VersionController : ControllerBase
{
    private readonly IStatusService _statusService;
    private readonly ILogger<VersionController> _logger;

    public VersionController(IStatusService statusService, ILogger<VersionController> logger)
    {
        _statusService = statusService;
        _logger = logger;
    }

    /// <summary>
    /// Get the current system version information.
    /// </summary>
    /// <returns>A <see cref="V3Envelope{T}"/> wrapping a <see cref="V3VersionResult"/>.</returns>
    /// <remarks>
    /// On error, returns the same envelope with the app version and an unknown storage version to maintain
    /// compatibility with clients that depend on this endpoint always returning 200.
    /// </remarks>
    /// <response code="200">Version information (always returns 200).</response>
    [HttpGet]
    [RemoteQuery]
    [NightscoutEndpoint("/api/v3/version")]
    [ProducesResponseType(typeof(V3Envelope<V3VersionResult>), 200)]
    public async Task<ActionResult<V3Envelope<V3VersionResult>>> GetVersion()
    {
        _logger.LogDebug(
            "V3 version endpoint requested from {RemoteIpAddress}",
            HttpContext.Connection.RemoteIpAddress
        );

        try
        {
            var status = await _statusService.GetV3SystemStatusAsync();

            _logger.LogDebug("Successfully returned version {Version}", status.Version);

            return Ok(
                new V3Envelope<V3VersionResult>
                {
                    Result = new V3VersionResult
                    {
                        Version = status.Version,
                        ApiVersion = status.ApiVersion,
                        SrvDate = status.SrvDate,
                        Storage = status.Storage,
                    },
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting version information");

            return Ok(
                new V3Envelope<V3VersionResult>
                {
                    Result = new V3VersionResult
                    {
                        Version = AppVersion.Current,
                        ApiVersion = ApplicationConstants.NightscoutApi.V3Version,
                        SrvDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Storage = new StorageInfo { Version = "unknown" },
                    },
                }
            );
        }
    }
}
