using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// V1 Notifications controller providing basic notification system endpoints.
/// Implements the legacy /api/v1/notifications and /api/v1/adminnotifies endpoints with 1:1 backwards compatibility.
/// Based on the legacy notifications.js and adminnotifies.js implementations.
/// </summary>
/// <seealso cref="INotificationV1Service"/>
[ApiController]
[Tags("V1")]
[Route("api/v1")]
[Produces("application/json")]
[Authorize(Policy = PolicyNames.HasPermissions)]
public class NotificationsController : ControllerBase
{
    private readonly INotificationV1Service _notificationService;
    private readonly ILogger<NotificationsController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="NotificationsController"/>.
    /// </summary>
    /// <param name="notificationService">Service handling legacy V1 notification operations.</param>
    /// <param name="logger">Logger instance.</param>
    public NotificationsController(
        INotificationV1Service notificationService,
        ILogger<NotificationsController> logger
    )
    {
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <summary>
    /// Acknowledge a notification alarm to silence it
    /// Implements the /notifications/ack endpoint from legacy notifications.js
    /// </summary>
    /// <param name="request">Acknowledgment request containing level, group, and silence time</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Acknowledgment response indicating success or failure</returns>
    /// <response code="200">Notification acknowledged successfully</response>
    /// <response code="400">Invalid acknowledgment request</response>
    /// <response code="500">Internal server error</response>
    [HttpPost("notifications/ack")]
    [Authorize]
    [NightscoutEndpoint("/api/v1/notifications/ack")]
    [ProducesResponseType(typeof(NotificationAckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotificationAckResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(
        typeof(NotificationAckResponse),
        StatusCodes.Status500InternalServerError
    )]
    public async Task<ActionResult<NotificationAckResponse>> AckNotification(
        [FromBody] NotificationAckRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var remoteAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        _logger.LogDebug(
            "Notification ack endpoint requested from {RemoteAddress} for level {Level}, group {Group}",
            remoteAddress,
            request?.Level,
            request?.Group
        );

        try
        {
            if (request == null)
            {
                _logger.LogWarning(
                    "Notification ack request body is null from {RemoteAddress}",
                    remoteAddress
                );
                return BadRequest(
                    new NotificationAckResponse
                    {
                        Success = false,
                        Message = "Request body is required",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }
                );
            }

            if (request.Level < 1 || request.Level > 2)
            {
                _logger.LogWarning(
                    "Invalid notification level {Level} from {RemoteAddress}",
                    request.Level,
                    remoteAddress
                );
                return BadRequest(
                    new NotificationAckResponse
                    {
                        Success = false,
                        Message = "Level must be 1 (WARN) or 2 (URGENT)",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }
                );
            }

            var response = await _notificationService.AckNotificationAsync(
                request,
                cancellationToken
            );

            if (!response.Success)
            {
                _logger.LogWarning(
                    "Notification ack failed from {RemoteAddress}: {Message}",
                    remoteAddress,
                    response.Message
                );
                return BadRequest(response);
            }

            _logger.LogDebug(
                "Notification acknowledged successfully from {RemoteAddress}",
                remoteAddress
            );
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error processing notification ack from {RemoteAddress}",
                remoteAddress
            );
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new NotificationAckResponse
                {
                    Success = false,
                    Message = "Internal server error processing acknowledgment",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            );
        }
    }

    /// <summary>
    /// Handle Pushover webhook callbacks for notification acknowledgments
    /// Implements the /notifications/pushovercallback endpoint for Pushover integration
    /// </summary>
    /// <param name="request">Pushover callback request data from webhook</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response indicating callback processing result</returns>
    /// <response code="200">Pushover callback processed successfully</response>
    /// <response code="400">Invalid callback request</response>
    /// <response code="500">Internal server error</response>
    [HttpPost("notifications/pushovercallback")]
    [AllowAnonymous]
    [NightscoutEndpoint("/api/v1/notifications/pushovercallback")]
    [ProducesResponseType(typeof(NotificationAckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotificationAckResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(
        typeof(NotificationAckResponse),
        StatusCodes.Status500InternalServerError
    )]
    public async Task<ActionResult<NotificationAckResponse>> PushoverCallback(
        [FromBody] PushoverCallbackRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var remoteAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        _logger.LogDebug(
            "Pushover callback endpoint requested from {RemoteAddress} with receipt {Receipt}",
            remoteAddress,
            request?.Receipt
        );

        try
        {
            if (request == null)
            {
                _logger.LogWarning(
                    "Pushover callback request body is null from {RemoteAddress}",
                    remoteAddress
                );
                return BadRequest(
                    new NotificationAckResponse
                    {
                        Success = false,
                        Message = "Request body is required",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }
                );
            }

            var response = await _notificationService.ProcessPushoverCallbackAsync(
                request,
                cancellationToken
            );

            _logger.LogDebug(
                "Pushover callback processed from {RemoteAddress}: {Success}",
                remoteAddress,
                response.Success
            );
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error processing Pushover callback from {RemoteAddress}",
                remoteAddress
            );
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new NotificationAckResponse
                {
                    Success = false,
                    Message = "Internal server error processing Pushover callback",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            );
        }
    }

    /// <summary>
    /// Get all admin notifications with their counts and timestamps
    /// Implements the /adminnotifies endpoint from legacy adminnotifies.js
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Admin notifications response with current notifications</returns>
    /// <response code="200">Admin notifications retrieved successfully</response>
    /// <response code="500">Internal server error</response>
    [HttpGet("adminnotifies")]
    [NightscoutEndpoint("/api/v1/adminnotifies")]
    [ProducesResponseType(typeof(AdminNotifiesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<AdminNotifiesResponse>> GetAdminNotifies(
        CancellationToken cancellationToken = default
    )
    {
        var remoteAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        _logger.LogDebug("Admin notifies endpoint requested from {RemoteAddress}", remoteAddress);

        try
        {
            // Get subject ID for authorization check (implements legacy ctx.authorization.resolveWithRequest)
            var subjectId = HttpContext?.GetSubjectIdString();

            var response = await _notificationService.GetAdminNotifiesAsync(
                subjectId,
                cancellationToken
            );

            _logger.LogDebug(
                "Admin notifies retrieved successfully from {RemoteAddress}, count: {Count}",
                remoteAddress,
                response.Message.NotifyCount
            );
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error retrieving admin notifies from {RemoteAddress}",
                remoteAddress
            );
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new AdminNotifiesResponse
                {
                    Status = 500,
                    Message = new AdminNotifiesMessage
                    {
                        Notifies = new List<AdminNotification>(),
                        NotifyCount = 0,
                    },
                }
            );
        }
    }

    /// <summary>
    /// Add a new admin notification or increment count if it already exists
    /// Provides an endpoint for creating admin notifications (typically used internally)
    /// </summary>
    /// <param name="notification">Admin notification to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response indicating whether the notification was added successfully</returns>
    /// <response code="200">Admin notification added successfully</response>
    /// <response code="400">Invalid notification request</response>
    /// <response code="500">Internal server error</response>
    [HttpPost("adminnotifies")]
    [Authorize]
    [NightscoutEndpoint("/api/v1/adminnotifies (POST)")]
    [ProducesResponseType(typeof(NotificationAckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotificationAckResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(
        typeof(NotificationAckResponse),
        StatusCodes.Status500InternalServerError
    )]
    public async Task<ActionResult<NotificationAckResponse>> AddAdminNotification(
        [FromBody] AdminNotification notification,
        CancellationToken cancellationToken = default
    )
    {
        var remoteAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        _logger.LogDebug(
            "Add admin notification endpoint requested from {RemoteAddress}",
            remoteAddress
        );

        try
        {
            if (notification == null)
            {
                _logger.LogWarning(
                    "Admin notification request body is null from {RemoteAddress}",
                    remoteAddress
                );
                return BadRequest(
                    new NotificationAckResponse
                    {
                        Success = false,
                        Message = "Request body is required",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }
                );
            }

            var response = await _notificationService.AddAdminNotificationAsync(
                notification,
                cancellationToken
            );

            if (!response.Success)
            {
                _logger.LogWarning(
                    "Admin notification add failed from {RemoteAddress}: {Message}",
                    remoteAddress,
                    response.Message
                );
                return BadRequest(response);
            }

            _logger.LogDebug(
                "Admin notification added successfully from {RemoteAddress}",
                remoteAddress
            );
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error adding admin notification from {RemoteAddress}",
                remoteAddress
            );
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new NotificationAckResponse
                {
                    Success = false,
                    Message = "Internal server error adding admin notification",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            );
        }
    }

    /// <summary>
    /// Clear all admin notifications
    /// Provides an endpoint for clearing all admin notifications (typically used for maintenance)
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response indicating whether the notifications were cleared successfully</returns>
    /// <response code="200">Admin notifications cleared successfully</response>
    /// <response code="500">Internal server error</response>
    [HttpDelete("adminnotifies")]
    [Authorize]
    [NightscoutEndpoint("/api/v1/adminnotifies (DELETE)")]
    [ProducesResponseType(typeof(NotificationAckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(
        typeof(NotificationAckResponse),
        StatusCodes.Status500InternalServerError
    )]
    public async Task<ActionResult<NotificationAckResponse>> ClearAllAdminNotifications(
        CancellationToken cancellationToken = default
    )
    {
        var remoteAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        _logger.LogDebug(
            "Clear all admin notifications endpoint requested from {RemoteAddress}",
            remoteAddress
        );

        try
        {
            await _notificationService.ClearAllAdminNotificationsAsync(cancellationToken);

            _logger.LogDebug(
                "All admin notifications cleared successfully from {RemoteAddress}",
                remoteAddress
            );
            return Ok(
                new NotificationAckResponse
                {
                    Success = true,
                    Message = "All admin notifications cleared successfully",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error clearing all admin notifications from {RemoteAddress}",
                remoteAddress
            );
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new NotificationAckResponse
                {
                    Success = false,
                    Message = "Internal server error clearing admin notifications",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            );
        }
    }

    /// <summary>
    /// Send a Pushover notification for testing or manual triggering
    /// Provides an endpoint for sending Pushover notifications directly
    /// </summary>
    /// <param name="request">Pushover notification request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response indicating whether the notification was sent successfully</returns>
    /// <response code="200">Pushover notification sent successfully</response>
    /// <response code="400">Invalid notification request</response>
    /// <response code="500">Internal server error</response>
    [HttpPost("notifications/pushover")]
    [Authorize]
    [NightscoutEndpoint("/api/v1/notifications/pushover")]
    [ProducesResponseType(typeof(NotificationAckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotificationAckResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(
        typeof(NotificationAckResponse),
        StatusCodes.Status500InternalServerError
    )]
    public async Task<ActionResult<NotificationAckResponse>> SendPushoverNotification(
        [FromBody] PushoverNotificationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var remoteAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
        _logger.LogDebug(
            "Send Pushover notification endpoint requested from {RemoteAddress}",
            remoteAddress
        );

        try
        {
            if (request == null)
            {
                _logger.LogWarning(
                    "Pushover notification request body is null from {RemoteAddress}",
                    remoteAddress
                );
                return BadRequest(
                    new NotificationAckResponse
                    {
                        Success = false,
                        Message = "Request body is required",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }
                );
            }

            if (string.IsNullOrEmpty(request.Title) || string.IsNullOrEmpty(request.Message))
            {
                _logger.LogWarning(
                    "Invalid Pushover notification request from {RemoteAddress}: missing title or message",
                    remoteAddress
                );
                return BadRequest(
                    new NotificationAckResponse
                    {
                        Success = false,
                        Message = "Title and message are required",
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }
                );
            }

            // Use level and group for alarm-based notifications, or default values
            var level = request.Level ?? 1;
            var group = request.Group ?? "manual";

            var response = await _notificationService.SendPushoverNotificationAsync(
                level,
                group,
                request.Title,
                request.Message,
                request.Sound,
                cancellationToken
            );

            if (!response.Success)
            {
                _logger.LogWarning(
                    "Pushover notification send failed from {RemoteAddress}: {Message}",
                    remoteAddress,
                    response.Message
                );
                return BadRequest(response);
            }

            _logger.LogDebug(
                "Pushover notification sent successfully from {RemoteAddress}",
                remoteAddress
            );
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error sending Pushover notification from {RemoteAddress}",
                remoteAddress
            );
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new NotificationAckResponse
                {
                    Success = false,
                    Message = "Internal server error sending Pushover notification",
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            );
        }
    }
}
