using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Models;
using Nocturne.API.Services.Connectors;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for querying the health and status of registered data connectors.
/// </summary>
/// <remarks>
/// Connector status is maintained by <see cref="IConnectorHealthService"/>, which tracks
/// the last-seen timestamp and error state for each connector (Dexcom, Glooko, Libre, etc.).
/// This endpoint is used by the frontend dashboard to display connector health indicators.
/// </remarks>
/// <seealso cref="IConnectorHealthService"/>
[Authorize]
[ApiController]
[Tags("Connectors")]
[Route("api/v4/connectors")]
public class ConnectorStatusController : ControllerBase
{
    private readonly IConnectorHealthService _healthService;
    private readonly ILogger<ConnectorStatusController> _logger;

    public ConnectorStatusController(
        IConnectorHealthService healthService,
        ILogger<ConnectorStatusController> logger
    )
    {
        _healthService = healthService;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current status and metrics for all registered connectors
    /// </summary>
    [HttpGet("status")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IEnumerable<ConnectorStatusDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ConnectorStatusDto>>> GetStatus(
        CancellationToken cancellationToken
    )
    {
        _logger.LogDebug("Fetching connector statuses");
        var statuses = await _healthService.GetConnectorStatusesAsync(cancellationToken);
        return Ok(statuses);
    }
}
