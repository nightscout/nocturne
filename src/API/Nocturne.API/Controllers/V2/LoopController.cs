using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V2;

/// <summary>
/// Operator diagnostics for the Loop APNs push configuration.
/// </summary>
/// <seealso cref="ILoopService"/>
[ApiController]
[Tags("V2")]
[Route("api/v2")]
[Produces("application/json")]
[Authorize(Policy = PolicyNames.HasPermissions)]
public class LoopController : ControllerBase
{
    private readonly ILoopService _loopService;

    /// <summary>
    /// Initializes a new instance of <see cref="LoopController"/>.
    /// </summary>
    /// <param name="loopService">Loop APNs push service.</param>
    public LoopController(ILoopService loopService)
    {
        _loopService = loopService;
    }

    /// <summary>
    /// Get Loop service configuration status
    /// Provides debugging information about Loop/APNS configuration
    /// </summary>
    /// <returns>Loop configuration status</returns>
    /// <response code="200">Configuration status retrieved successfully</response>
    [HttpGet("loop/status")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    // APNS configuration diagnostics, not alert data. Gated like DebugController.
    [RequireAdmin]
    public ActionResult<object> GetLoopStatus()
    {
        var status = _loopService.GetConfigurationStatus();
        return Ok(status);
    }
}
