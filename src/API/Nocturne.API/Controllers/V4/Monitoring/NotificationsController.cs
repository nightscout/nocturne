using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// Controller for managing in-app notifications.
/// </summary>
/// <remarks>
/// <para>
/// Creating a notification requires <see cref="OAuthScopes.AlertsReadWrite"/>: an in-app
/// notification is a delivery channel for an alert, so minting one puts an alert in front of a
/// user. The per-action <c>[Authorize]</c> alone is satisfied by any credential carrying a subject,
/// including a read-only follower token holding nothing but <c>glucose.read</c>.
/// </para>
/// <para>
/// <see cref="ExecuteAction"/> additionally accepts <see cref="OAuthScopes.DeviceNotify"/>, for the
/// same reason <see cref="AlertsController.AcknowledgeExcursion"/> does, but only for an
/// <c>alert.firing</c> notification — see that action's remarks.
/// </para>
/// <para>
/// <see cref="MarkAsRead"/>, <see cref="MarkAllAsRead"/> and <see cref="DismissNotification"/>
/// carry no scope gate. They write only the caller's own bookkeeping — <c>read_at</c>, and the
/// archive flags — on a row the service confines to the caller's subject, and they change no alert
/// state: archiving an <c>alert.firing</c> notification does not acknowledge or silence the
/// excursion behind it. Gating them on <c>alerts.readwrite</c> left every role that receives alerts
/// without holding that scope with a bell badge it could never clear.
/// </para>
/// <para>
/// Every action here resolves the caller through <see cref="HttpContextExtensions.GetSubjectIdString"/>
/// and returns 401 when it is absent, so a guest session — which authenticates with
/// <c>SubjectId = null</c> — reaches none of them.
/// </para>
/// </remarks>
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
    [RequireScope(OAuthScopes.AlertsReadWrite)]
    [ProducesResponseType(typeof(InAppNotificationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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
    /// <remarks>
    /// <para>
    /// Accepts <see cref="OAuthScopes.DeviceNotify"/> as well as
    /// <see cref="OAuthScopes.AlertsReadWrite"/>, because this is the second path to the excursion
    /// acknowledgement <see cref="AlertsController.AcknowledgeExcursion"/> serves: the Acknowledge
    /// action on an <c>alert.firing</c> in-app notification dispatches through
    /// <see cref="IInAppNotificationService.ExecuteActionAsync"/> to
    /// <c>AlertActionHandler</c>, which calls
    /// <see cref="Core.Contracts.Alerts.IAlertAcknowledgementService.AcknowledgeExcursionAsync"/>.
    /// The resolver grants <c>device.notify</c> to any member holding at least one permission, and
    /// the Clinician and Viewer seed roles hold it outright, so requiring
    /// <c>alerts.readwrite</c> alone gave those members an alert they could see but not stop.
    /// </para>
    /// <para>
    /// <see cref="IInAppNotificationService.ExecuteActionAsync"/> is a generic dispatcher, not an
    /// acknowledgement endpoint: past the built-in <c>dismiss</c>/<c>navigate</c> cases it hands the
    /// action to whichever <see cref="INotificationActionHandler"/> is registered for the
    /// notification's type, and those handlers mutate their own domains — the tracker handler's
    /// <c>accept</c> completes and restarts tracker instances, a write
    /// <c>TrackersController</c> gates on <c>alerts.readwrite</c>. So the capability arm is confined
    /// here to <see cref="InAppProvider.NotificationType"/>: a caller holding only
    /// <c>device.notify</c> is refused any other notification type, and only a holder of
    /// <c>alerts.readwrite</c> reaches the rest of the dispatch table.
    /// </para>
    /// <para>
    /// Ownership is enforced downstream rather than by the scope: <c>ExecuteActionAsync</c> rejects
    /// the call unless <c>notification.UserId</c> equals the caller's subject, so the wider gate
    /// reaches only the caller's own notifications.
    /// </para>
    /// </remarks>
    [HttpPost("{id:guid}/actions/{actionId}")]
    [RemoteCommand(Invalidates = ["GetNotifications"])]
    [Authorize]
    [RequireScope(OAuthScopes.AlertsReadWrite, OAuthScopes.DeviceNotify)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ExecuteAction(Guid id, string actionId)
    {
        var userId = HttpContext.GetSubjectIdString();

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        if (!HttpContext.HasScope(OAuthScopes.AlertsReadWrite))
        {
            var type = await _notificationService.GetNotificationTypeAsync(
                id,
                userId,
                HttpContext.RequestAborted
            );

            if (type == null)
            {
                return NotFound();
            }

            if (!string.Equals(type, InAppProvider.NotificationType, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "User {UserId} holding only device.notify attempted action {ActionId} on {Type} notification {NotificationId}",
                    userId,
                    actionId,
                    type,
                    id
                );
                return Forbid();
            }
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

    /// <inheritdoc cref="IInAppNotificationService.MarkAllAsReadAsync"/>
    [HttpPost("read-all")]
    [RemoteCommand(Invalidates = ["GetNotifications"])]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> MarkAllAsRead()
    {
        var userId = HttpContext.GetSubjectIdString();

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        await _notificationService.MarkAllAsReadAsync(userId, HttpContext.RequestAborted);

        return NoContent();
    }

    /// <inheritdoc cref="IInAppNotificationService.MarkAsReadAsync"/>
    [HttpPost("{id:guid}/read")]
    [RemoteCommand(Invalidates = ["GetNotifications"])]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> MarkAsRead(Guid id)
    {
        var userId = HttpContext.GetSubjectIdString();

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var success = await _notificationService.MarkAsReadAsync(
            id,
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
            userId,
            HttpContext.RequestAborted
        );

        if (!success)
        {
            return NotFound();
        }

        return NoContent();
    }
}
