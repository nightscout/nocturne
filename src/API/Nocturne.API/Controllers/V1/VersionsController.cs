using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Versions controller that provides 1:1 compatibility with Nightscout versions endpoint.
/// </summary>
/// <seealso cref="IVersionService"/>
/// <seealso cref="ApiVersionEntry"/>
[ApiController]
[Tags("V1")]
[Route("api/[controller]")]
[AllowAnonymous]
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
    /// Get the API levels served, as a JSON array of <c>version</c> and <c>url</c>.
    /// </summary>
    /// <returns>One entry per supported API level</returns>
    [HttpGet]
    [NightscoutEndpoint("/api/versions")]
    [ProducesResponseType(typeof(IReadOnlyList<ApiVersionEntry>), 200)]
    public async Task<ActionResult<IReadOnlyList<ApiVersionEntry>>> GetVersions()
    {
        _logger.LogDebug(
            "Versions endpoint requested from {RemoteIpAddress}",
            HttpContext.Connection.RemoteIpAddress
        );

        return Ok(await _versionService.GetSupportedVersionsAsync());
    }
}
