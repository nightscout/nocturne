using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using Nocturne.API.Services;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for tracker alert management
/// </summary>
[ApiController]
[Authorize]
[Route("api/v4/trackers/alerts")]
[Tags("V4 Tracker Alerts")]
public class TrackerAlertsController : ControllerBase
{
    private readonly ITrackerAlertService _alertService;
    private readonly ILogger<TrackerAlertsController> _logger;

    // Available alert sounds (could be moved to config or database)
    private static readonly string[] AvailableSounds =
    [
        "chime",
        "alert",
        "urgent",
        "gentle",
        "notification",
        "beep",
        "tone"
    ];

    public TrackerAlertsController(
        ITrackerAlertService alertService,
        ILogger<TrackerAlertsController> logger)
    {
        _alertService = alertService;
        _logger = logger;
    }

    /// <summary>
    /// Get pending tracker alerts for the current user
    /// </summary>
    /// <returns>List of pending alerts that need attention</returns>
    [HttpGet("pending")]
    [ProducesResponseType(typeof(TrackerAlertDto[]), 200)]
    public async Task<ActionResult<TrackerAlertDto[]>> GetPendingAlerts()
    {
        var userId = HttpContext.GetSubjectIdString()!;
        var alerts = await _alertService.GetPendingAlertsAsync(userId, HttpContext.RequestAborted);

        return Ok(alerts.Select(a => new TrackerAlertDto
        {
            InstanceId = a.InstanceId,
            DefinitionId = a.DefinitionId,
            ThresholdId = a.ThresholdId,
            TrackerName = a.TrackerName,
            Urgency = a.Urgency,
            Message = a.Message,
            PushEnabled = a.Config.PushEnabled,
            AudioEnabled = a.Config.AudioEnabled,
            AudioSound = a.Config.AudioSound,
            VibrateEnabled = a.Config.VibrateEnabled,
        }).ToArray());
    }

    /// <summary>
    /// Get available alert sounds
    /// </summary>
    /// <returns>List of available sound preset names</returns>
    [HttpGet("sounds")]
    [ProducesResponseType(typeof(string[]), 200)]
    public ActionResult<string[]> GetAvailableSounds()
    {
        return Ok(AvailableSounds);
    }
}

/// <summary>
/// DTO for tracker alerts returned to the frontend
/// </summary>
public class TrackerAlertDto
{
    public Guid InstanceId { get; set; }
    public Guid DefinitionId { get; set; }
    public Guid ThresholdId { get; set; }
    public string TrackerName { get; set; } = string.Empty;
    public NotificationUrgency Urgency { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool PushEnabled { get; set; }
    public bool AudioEnabled { get; set; }
    public string? AudioSound { get; set; }
    public bool VibrateEnabled { get; set; }
}
