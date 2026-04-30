using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Contracts.Alerts;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// What-if replay of the tenant's enabled alert rules over a historical window.
/// Returns the events that <em>would</em> have fired had the current rule set been active.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v4/alerts/replay")]
public class AlertReplayController : ControllerBase
{
    private readonly IAlertReplayService _replayService;

    public AlertReplayController(IAlertReplayService replayService)
    {
        _replayService = replayService;
    }

    /// <summary>
    /// Replay enabled rules over a window. <c>date=null</c> replays the rolling last 24 hours;
    /// otherwise replays that calendar day in <c>timezone</c> (UTC if omitted).
    /// </summary>
    [HttpPost]
    [RemoteCommand]
    [ProducesResponseType(typeof(AlertReplayResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<AlertReplayResult>> Replay(
        [FromBody] AlertReplayRequest request, CancellationToken ct)
    {
        var result = await _replayService.ReplayAsync(request.Date, request.Timezone, ct);
        return Ok(result);
    }
}

/// <summary>
/// Request body for the alerts replay endpoint.
/// </summary>
public record AlertReplayRequest(DateOnly? Date, string? Timezone);
