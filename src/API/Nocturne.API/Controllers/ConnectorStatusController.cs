using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Models;
using Nocturne.API.Services;

namespace Nocturne.API.Controllers;

[ApiController]
[Route("api/v1/connectors")]
[Tags("V1 Connector Status")]
public class ConnectorStatusController : ControllerBase
{
    private readonly IConnectorHealthService _healthService;
    private readonly ILogger<ConnectorStatusController> _logger;

    public ConnectorStatusController(
        IConnectorHealthService healthService,
        ILogger<ConnectorStatusController> logger)
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
    public async Task<ActionResult<IEnumerable<ConnectorStatusDto>>> GetStatus(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching connector statuses");
        var statuses = await _healthService.GetConnectorStatusesAsync(cancellationToken);
        return Ok(statuses);
    }
}
