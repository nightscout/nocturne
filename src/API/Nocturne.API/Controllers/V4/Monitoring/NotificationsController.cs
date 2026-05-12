using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// Controller for managing in-app notifications.
/// </summary>
/// <seealso cref="IInAppNotificationService"/>
[ApiController]
[Tags("Monitoring")]
[Route("api/v4/notifications")]
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
    /// Create a notification programmatically (for integrations and services)
    /// </summary>
    [HttpPost]
    [Authorize]
    [ProducesResponseType(typeof(InAppNotificationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> CreateNotification(
        [FromBody] CreateNotificationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetSubjectIdString();

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        try
        {
            var notification = await _notificationService.CreateNotificationAsync(
                userId,
                request.Type,
                request.Title,
                category: request.Category,
                urgency: request.Urgency,
                icon: request.Icon,
                source: request.Source,
                subtitle: request.Subtitle,
                sourceId: request.SourceId,
                actions: request.Actions,
                resolutionConditions: request.ResolutionConditions,
                metadata: request.Metadata,
                cancellationToken: cancellationToken);

            return Created($"/api/v4/notifications/{notification.Id}", notification);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Rate limit"))
        {
            return StatusCode(429, new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <inheritdoc cref="IInAppNotificationService.GetActiveNotificationsAsync"/>
    [HttpGet]
    [RemoteQuery]
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

    /// <inheritdoc cref="IInAppNotificationService.ExecuteActionAsync"/>
    [HttpPost("{id:guid}/actions/{actionId}")]
    [RemoteCommand(Invalidates = ["GetNotifications"])]
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

    /// <inheritdoc cref="IInAppNotificationService.ArchiveNotificationAsync"/>
    [HttpDelete("{id:guid}")]
    [RemoteCommand(Invalidates = ["GetNotifications"])]
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
