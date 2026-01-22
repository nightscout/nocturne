using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for managing in-app notifications
/// </summary>
[ApiController]
[Route("api/v4/notifications")]
[Tags("V4 Notifications")]
public class NotificationsController : ControllerBase
{
    private readonly IInAppNotificationService _notificationService;
    private readonly ILogger<NotificationsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationsController"/> class.
    /// </summary>
    /// <param name="notificationService">The notification service</param>
    /// <param name="logger">The logger</param>
    public NotificationsController(
        IInAppNotificationService notificationService,
        ILogger<NotificationsController> logger
    )
    {
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <summary>
    /// Get all active notifications for the current user
    /// </summary>
    /// <returns>List of active notifications</returns>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(typeof(List<InAppNotificationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<InAppNotificationDto>>> GetNotifications()
    {
        var userId = HttpContext.GetSubjectIdString();

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var notifications = await _notificationService.GetActiveNotificationsAsync(
            userId,
            HttpContext.RequestAborted
        );

        return Ok(notifications);
    }

    /// <summary>
    /// Execute an action on a notification
    /// </summary>
    /// <param name="id">The notification ID</param>
    /// <param name="actionId">The action ID to execute</param>
    /// <returns>No content if successful</returns>
    [HttpPost("{id:guid}/actions/{actionId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ExecuteAction(Guid id, string actionId)
    {
        var userId = HttpContext.GetSubjectIdString();

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        _logger.LogDebug(
            "User {UserId} executing action {ActionId} on notification {NotificationId}",
            userId,
            actionId,
            id
        );

        var success = await _notificationService.ExecuteActionAsync(
            id,
            actionId,
            userId,
            HttpContext.RequestAborted
        );

        if (!success)
        {
            return NotFound();
        }

        return NoContent();
    }

    /// <summary>
    /// Dismiss a notification (archive with dismissed reason)
    /// </summary>
    /// <param name="id">The notification ID to dismiss</param>
    /// <returns>No content if successful</returns>
    [HttpDelete("{id:guid}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DismissNotification(Guid id)
    {
        var userId = HttpContext.GetSubjectIdString();

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        _logger.LogDebug(
            "User {UserId} dismissing notification {NotificationId}",
            userId,
            id
        );

        var success = await _notificationService.ArchiveNotificationAsync(
            id,
            NotificationArchiveReason.Dismissed,
            HttpContext.RequestAborted
        );

        if (!success)
        {
            return NotFound();
        }

        return NoContent();
    }
}
