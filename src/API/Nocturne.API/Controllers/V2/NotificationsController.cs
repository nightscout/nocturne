using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V2;

/// <summary>
/// V2 Notifications controller providing enhanced notifications system endpoints
/// Implements the legacy /api/v2/notifications endpoints with 1:1 backwards compatibility
/// Based on the legacy notifications-v2.js implementation
/// </summary>
[ApiController]
[Route("api/v2/notifications")]
[Produces("application/json")]
[Tags("V2 Notifications")]
[ClientPropertyName("v2Notifications")]
public class NotificationsController : ControllerBase
{
    private readonly INotificationV2Service _notificationService;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        INotificationV2Service notificationService,
        ILogger<NotificationsController> logger
    )
    {
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <summary>
    /// Send Loop notification for iOS Loop app integration
    /// Implements the /api/v2/notifications/loop endpoint from legacy notifications-v2.js
    /// </summary>
    /// <param name="request">Loop notification request data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Notification response indicating success or failure</returns>
    /// <response code="200">Notification processed successfully</response>
    /// <response code="400">Invalid notification request</response>
    /// <response code="500">Internal server error</response>
    [HttpPost("loop")]
    [NightscoutEndpoint("/api/v2/notifications/loop")]
    [ProducesResponseType(typeof(NotificationV2Response), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotificationV2Response), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(NotificationV2Response), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<NotificationV2Response>> SendLoopNotification(
        [FromBody] LoopNotificationRequest request,
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
            if (request == null)
            {
                _logger.LogWarning(
                    "Loop notification request body is null from {RemoteAddress}",
                    remoteAddress
                );
                return BadRequest(
                    new NotificationV2Response
                    {
                        Success = false,
                        Message = "Request body is required",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }
                );
            }

            var response = await _notificationService.SendLoopNotificationAsync(
                request,
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
                "Loop notification processed successfully from {RemoteAddress}",
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
                new NotificationV2Response
                {
                    Success = false,
                    Message = "Internal server error processing Loop notification",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            );
        }
    }

    /// <summary>
    /// Process a generic V2 notification
    /// Provides a generic endpoint for processing various notification types
    /// </summary>
    /// <param name="notification">Notification data to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Notification response indicating success or failure</returns>
    /// <response code="200">Notification processed successfully</response>
    /// <response code="400">Invalid notification request</response>
    /// <response code="500">Internal server error</response>
    [HttpPost]
    [NightscoutEndpoint("/api/v2/notifications")]
    [ProducesResponseType(typeof(NotificationV2Response), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotificationV2Response), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(NotificationV2Response), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<NotificationV2Response>> ProcessNotification(
        [FromBody] NotificationBase notification,
        CancellationToken cancellationToken = default
    )
    {
        var remoteAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        _logger.LogDebug("V2 notification endpoint requested from {RemoteAddress}", remoteAddress);

        try
        {
            if (notification == null)
            {
                _logger.LogWarning(
                    "Notification request body is null from {RemoteAddress}",
                    remoteAddress
                );
                return BadRequest(
                    new NotificationV2Response
                    {
                        Success = false,
                        Message = "Request body is required",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }
                );
            }

            var response = await _notificationService.ProcessNotificationAsync(
                notification,
                cancellationToken
            );

            if (!response.Success)
            {
                _logger.LogWarning(
                    "Notification processing failed from {RemoteAddress}: {Message}",
                    remoteAddress,
                    response.Message
                );
                return BadRequest(response);
            }

            _logger.LogDebug(
                "Notification processed successfully from {RemoteAddress}",
                remoteAddress
            );
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error processing notification from {RemoteAddress}",
                remoteAddress
            );
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new NotificationV2Response
                {
                    Success = false,
                    Message = "Internal server error processing notification",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            );
        }
    }

    /// <summary>
    /// Get current notification system status and configuration
    /// Provides information about the notification system capabilities and status
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current notification system status</returns>
    /// <response code="200">Notification status retrieved successfully</response>
    /// <response code="500">Internal server error</response>
    [HttpGet("status")]
    [NightscoutEndpoint("/api/v2/notifications/status")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<object>> GetNotificationStatus(
        CancellationToken cancellationToken = default
    )
    {
        var remoteAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        _logger.LogDebug(
            "Notification status endpoint requested from {RemoteAddress}",
            remoteAddress
        );

        try
        {
            var status = await _notificationService.GetNotificationStatusAsync(cancellationToken);

            _logger.LogDebug(
                "Notification status retrieved successfully from {RemoteAddress}",
                remoteAddress
            );
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error retrieving notification status from {RemoteAddress}",
                remoteAddress
            );
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    error = "Internal server error retrieving notification status",
                    timestamp = DateTimeOffset.UtcNow.ToString("O"),
                }
            );
        }
    }
}
