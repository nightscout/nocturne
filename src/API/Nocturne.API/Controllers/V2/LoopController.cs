using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Authorization;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V2;

/// <summary>
/// Loop controller providing Apple Push Notification Service (APNS) integration for iOS Loop app.
/// Implements the legacy loop.sendNotification() functionality with 1:1 backwards compatibility.
/// Based on the legacy loop.js implementation.
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
    private readonly ILogger<LoopController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="LoopController"/>.
    /// </summary>
    /// <param name="loopService">Service for sending Loop APNS notifications.</param>
    /// <param name="logger">Logger instance.</param>
    public LoopController(ILoopService loopService, ILogger<LoopController> logger)
    {
        _loopService = loopService;
        _logger = logger;
    }

    /// <summary>
    /// Send Loop notification directly to iOS Loop app via APNS
    /// Implements the legacy ctx.loop.sendNotification() functionality with 1:1 compatibility
    /// This endpoint expects the exact data structure used by the legacy loop.js implementation
    /// </summary>
    /// <param name="request">Loop notification data and settings</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Loop notification response indicating success or failure</returns>
    /// <response code="200">Loop notification sent successfully</response>
    /// <response code="400">Invalid Loop notification request</response>
    /// <response code="500">Internal server error</response>
    [HttpPost("loop/send")]
    [Authorize]
    [ProducesResponseType(typeof(LoopNotificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LoopNotificationResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(
        typeof(LoopNotificationResponse),
        StatusCodes.Status500InternalServerError
    )]
    public async Task<ActionResult<LoopNotificationResponse>> SendLoopNotification(
        [FromBody] LoopSendRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var remoteAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        _logger.LogDebug(
            "Loop notification endpoint requested from {RemoteAddress}",
            remoteAddress
        );

        try
        {
            if (request?.Data == null || request.LoopSettings == null)
            {
                _logger.LogWarning(
                    "Loop notification request missing required data or loopSettings from {RemoteAddress}",
                    remoteAddress
                );
                return BadRequest(
                    new LoopNotificationResponse
                    {
                        Success = false,
                        Message = "Request must include both 'data' and 'loopSettings'",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }
                );
            }

            var response = await _loopService.SendNotificationAsync(
                request.Data,
                request.LoopSettings,
                remoteAddress,
                cancellationToken
            );

            if (!response.Success)
            {
                _logger.LogWarning(
                    "Loop notification failed from {RemoteAddress}: {Message}",
                    remoteAddress,
                    response.Message
                );
                return BadRequest(response);
            }

            _logger.LogDebug(
                "Loop notification sent successfully from {RemoteAddress}",
                remoteAddress
            );
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error processing Loop notification from {RemoteAddress}",
                remoteAddress
            );
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new LoopNotificationResponse
                {
                    Success = false,
                    Message = "Internal server error processing Loop notification",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            );
        }
    }

    /// <summary>
    /// Get Loop service configuration status
    /// Provides debugging information about Loop/APNS configuration
    /// </summary>
    /// <returns>Loop configuration status</returns>
    /// <response code="200">Configuration status retrieved successfully</response>
    [HttpGet("loop/status")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public ActionResult<object> GetLoopStatus()
    {
        var status = _loopService.GetConfigurationStatus();
        return Ok(status);
    }
}

/// <summary>
/// Loop notification request model for the direct Loop endpoint
/// Combines the data and loopSettings for a complete notification request
/// </summary>
public class LoopSendRequest
{
    /// <summary>
    /// Loop notification data (matches legacy data structure)
    /// </summary>
    public LoopNotificationData Data { get; set; } = new();

    /// <summary>
    /// Loop settings from user profile containing device token and bundle ID
    /// </summary>
    public LoopSettings LoopSettings { get; set; } = new();
}
