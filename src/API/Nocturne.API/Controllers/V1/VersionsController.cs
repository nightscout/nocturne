using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Versions controller that provides 1:1 compatibility with Nightscout versions endpoint.
/// </summary>
/// <seealso cref="IVersionService"/>
/// <seealso cref="VersionsResponse"/>
[ApiController]
[Tags("V1")]
[Route("api/[controller]")]
public class VersionsController : ControllerBase
{
    private readonly IVersionService _versionService;
    private readonly ILogger<VersionsController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="VersionsController"/>.
    /// </summary>
    /// <param name="versionService">Service providing API version information.</param>
    /// <param name="logger">Logger instance.</param>
    public VersionsController(IVersionService versionService, ILogger<VersionsController> logger)
    {
        _versionService = versionService;
        _logger = logger;
    }

    /// <summary>
    /// Get the list of supported API versions
    /// </summary>
    /// <returns>List of supported API versions</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/versions")]
    [ProducesResponseType(typeof(VersionsResponse), 200)]
    public async Task<ActionResult<VersionsResponse>> GetVersions()
    {
        _logger.LogDebug(
            "Versions endpoint requested from {RemoteIpAddress}",
            HttpContext.Connection.RemoteIpAddress
        );

        try
        {
            var versions = await _versionService.GetSupportedVersionsAsync();

            _logger.LogDebug(
                "Successfully returned {VersionCount} supported versions",
                versions.Versions.Count
            );

            return Ok(versions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting supported versions");

            // Return minimal version list even on error to maintain compatibility
            return Ok(new VersionsResponse { Versions = new List<string> { "1" } });
        }
    }
}
