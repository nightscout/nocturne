using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Controllers.V4.Platform;

/// <summary>
/// Controller for system-level lifecycle signals such as bot heartbeats.
/// </summary>
/// <remarks>
/// <c>POST /heartbeat</c> is called by the <c>@nocturne/bot</c> process on each polling cycle
/// to report which platforms (Discord, Telegram, etc.) are currently online. The
/// <see cref="BotHealthService"/> records these timestamps for health-check display in the admin UI.
/// </remarks>
/// <seealso cref="BotHealthService"/>
[ApiController]
[Tags("Platform")]
[Authorize]
[Route("api/v4/system")]
public class SystemController(BotHealthService botHealth) : ControllerBase
{
    [HttpPost("heartbeat")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Heartbeat([FromBody] HeartbeatRequest request)
    {
        botHealth.Record(request.Platforms);
        return Ok();
    }

    [HttpGet("channels")]
    [RemoteQuery]
    [ProducesResponseType(typeof(ChannelStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<ChannelStatusResponse> GetChannelStatuses()
    {
        var statuses = botHealth.GetChannelStatuses();
        return Ok(new ChannelStatusResponse { Channels = statuses });
    }
}

public class HeartbeatRequest
{
    public string[] Platforms { get; set; } = [];
    public string Service { get; set; } = string.Empty;
}

public class ChannelStatusResponse
{
    public IReadOnlyList<ChannelStatusEntry> Channels { get; set; } = [];
}
