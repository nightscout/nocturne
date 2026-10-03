using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.Core.Models.Authorization;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V2;

/// <summary>
/// V2 Notifications controller providing enhanced notifications system endpoints.
/// Implements the legacy /api/v2/notifications endpoints with 1:1 backwards compatibility.
/// Based on the legacy notifications-v2.js implementation.
/// </summary>
/// <seealso cref="INotificationV2Service"/>
[ApiController]
[Tags("V2")]
[Route("api/v2/notifications")]
[Produces("application/json")]
[ClientPropertyName("v2Notifications")]
[Authorize(Policy = PolicyNames.HasPermissions)]
public class NotificationsController : ControllerBase
{
    private readonly INotificationV2Service _notificationService;
    private readonly ILogger<NotificationsController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="NotificationsController"/>.
    /// </summary>
    /// <param name="notificationService">Service handling V2 notification operations.</param>
    /// <param name="logger">Logger instance.</param>
    public NotificationsController(
        INotificationV2Service notificationService,
        ILogger<NotificationsController> logger
    )
    {
        _notificationService = notificationService;
        _logger = logger;
    }

    /// <summary>
    /// Pushes a Loop remote command (override, override cancel, remote carbs, remote bolus) to the
    /// phone named by the current profile's <c>loopSettings</c>.
    /// </summary>
    /// <remarks>
    /// The body is read by hand because Nightscout's careportal posts it form-urlencoded while
    /// NightscoutKit posts JSON, and a <c>[FromBody]</c> parameter accepts only the latter.
    /// </remarks>
    /// <response code="200">The push was accepted by APNs; the body is <c>OK</c>.</response>
    /// <response code="400">The body is not valid JSON or form data.</response>
    /// <response code="500">Nothing was pushed; the body is the reason.</response>
    [HttpPost("loop")]
    [Authorize]
    [RequireScope(Scope.AlertsReadWrite)]
    [NightscoutEndpoint("/api/v2/notifications/loop")]
    [Consumes("application/json", "application/x-www-form-urlencoded")]
    [Produces("text/plain")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(string), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SendLoopNotification(CancellationToken cancellationToken = default)
    {
        var remoteAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        LoopNotificationData data;
        try
        {
            data = await ReadLoopNotificationAsync(Request, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return PlainText(StatusCodes.Status400BadRequest, "Malformed request body");
        }

        var response = await _notificationService.SendLoopNotificationAsync(
            data,
            remoteAddress,
            cancellationToken
        );

        if (!response.Success)
        {
            _logger.LogWarning(
                "Loop notification not sent for {RemoteAddress}: {Message}",
                remoteAddress,
                response.Message
            );
            return PlainText(StatusCodes.Status500InternalServerError, response.Message);
        }

        return PlainText(StatusCodes.Status200OK, "OK");
    }

    private static async Task<LoopNotificationData> ReadLoopNotificationAsync(
        HttpRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(cancellationToken);
            var fields = form.ToDictionary(field => field.Key, field => field.Value.ToString());
            return JsonSerializer.SerializeToElement(fields).Deserialize<LoopNotificationData>()
                ?? new LoopNotificationData();
        }

        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(body)
            ? new LoopNotificationData()
            : JsonSerializer.Deserialize<LoopNotificationData>(body) ?? new LoopNotificationData();
    }

    private static ContentResult PlainText(int statusCode, string content) =>
        new() { StatusCode = statusCode, Content = content, ContentType = "text/plain; charset=utf-8" };

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
    [Authorize]
    [RequireScope(Scope.AlertsReadWrite)]
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
    // Operator diagnostics, not alert data: alerts.read would be the wrong category, and no
    // production grant carries it. Gated like DebugController.
    [RequireAdmin]
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
