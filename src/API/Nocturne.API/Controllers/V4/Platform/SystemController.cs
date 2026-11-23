using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Controllers.V4.Platform;

/// <summary>
/// Platform lifecycle signals, such as heartbeats from the bot. Send a heartbeat, and read the status of each channel.
/// </summary>
/// <remarks>
/// <c>POST /heartbeat</c> is called by the <c>@nocturne/bot</c> process on each polling cycle
/// to report which platforms (Discord, Telegram, etc.) are currently online. The
/// <see cref="BotHealthService"/> records the time of the last heartbeat, and <c>GET /channels</c>
/// derives each channel's status from it for the alert channel components.
/// </remarks>
/// <seealso cref="BotHealthService"/>
[ApiController]
[Tags("Platform")]
[Authorize]
[Route("api/v4/system")]
public class SystemController(BotHealthService botHealth) : ControllerBase
{
    [HttpPost("heartbeat")]
    [RequireInstanceKeyAuth]
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
