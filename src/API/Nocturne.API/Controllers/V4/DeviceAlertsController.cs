using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for device alert management operations
/// </summary>
[ApiController]
[Authorize]
[Route("api/v4/devices/alerts")]
[Tags("V4 Device Alerts")]
public class DeviceAlertsController : ControllerBase
{
    private readonly IDeviceAlertEngine _alertEngine;
    private readonly IDeviceRegistryService _deviceRegistryService;
    private readonly NotificationPreferencesRepository _preferencesRepository;
    private readonly ILogger<DeviceAlertsController> _logger;

    /// <summary>
    /// Initializes a new instance of the DeviceAlertsController
    /// </summary>
    /// <param name="alertEngine">Alert engine service</param>
    /// <param name="deviceRegistryService">Device registry service</param>
    /// <param name="preferencesRepository">Notification preferences repository</param>
    /// <param name="logger">Logger</param>
    public DeviceAlertsController(
        IDeviceAlertEngine alertEngine,
        IDeviceRegistryService deviceRegistryService,
        NotificationPreferencesRepository preferencesRepository,
        ILogger<DeviceAlertsController> logger
    )
    {
        _alertEngine = alertEngine;
        _deviceRegistryService = deviceRegistryService;
        _preferencesRepository = preferencesRepository;
        _logger = logger;
    }

    /// <summary>
    /// Get active device alerts for the current user
    /// </summary>
    /// <param name="deviceId">Optional device ID filter</param>
    /// <returns>List of active device alerts</returns>
    [HttpGet]
    public async Task<ActionResult<List<DeviceAlert>>> GetActiveDeviceAlerts(
        [FromQuery] string? deviceId = null
    )
    {
        try
        {
            var userId = HttpContext.GetSubjectIdString()!;

            var alerts = new List<DeviceAlert>();

            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                // Get alerts for specific device
                var device = await _deviceRegistryService.GetDeviceAsync(
                    deviceId,
                    HttpContext.RequestAborted
                );
                if (device == null)
                {
                    return NotFound($"Device {deviceId} not found");
                }

                if (device.UserId != userId && !HttpContext.IsAdmin())
                {
                    return Forbid();
                }

                var deviceAlerts = await _alertEngine.ProcessDeviceAlertsAsync(
                    device,
                    HttpContext.RequestAborted
                );
                alerts.AddRange(deviceAlerts);
            }
            else
            {
                // Get alerts for all user devices
                var userDevices = await _deviceRegistryService.GetUserDevicesAsync(
                    userId,
                    HttpContext.RequestAborted
                );

                foreach (var device in userDevices)
                {
                    var deviceAlerts = await _alertEngine.ProcessDeviceAlertsAsync(
                        device,
                        HttpContext.RequestAborted
                    );
                    alerts.AddRange(deviceAlerts);
                }
            }

            // Filter to only unacknowledged alerts
            var activeAlerts = alerts.Where(a => !a.Acknowledged).ToList();

            return Ok(activeAlerts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active device alerts");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get device alerts summary by type
    /// </summary>
    /// <returns>Alert summary</returns>
    [HttpGet("summary")]
    public async Task<ActionResult<DeviceAlertSummary>> GetDeviceAlertSummary()
    {
        try
        {
            var userId = HttpContext.GetSubjectIdString()!;

            var summary = new DeviceAlertSummary
            {
                TotalDevices = 0,
                ActiveAlerts = 0,
                CriticalAlerts = 0,
                WarningAlerts = 0,
                AlertsByType = new Dictionary<DeviceAlertType, int>(),
            };

            var userDevices = await _deviceRegistryService.GetUserDevicesAsync(
                userId,
                HttpContext.RequestAborted
            );
            summary.TotalDevices = userDevices.Count;

            var allAlerts = new List<DeviceAlert>();
            foreach (var device in userDevices)
            {
                var deviceAlerts = await _alertEngine.ProcessDeviceAlertsAsync(
                    device,
                    HttpContext.RequestAborted
                );
                allAlerts.AddRange(deviceAlerts.Where(a => !a.Acknowledged));
            }

            summary.ActiveAlerts = allAlerts.Count;
            summary.CriticalAlerts = allAlerts.Count(a =>
                a.Severity == DeviceIssueSeverity.Critical
            );
            summary.WarningAlerts = allAlerts.Count(a =>
                a.Severity == DeviceIssueSeverity.High || a.Severity == DeviceIssueSeverity.Medium
            );

            // Group alerts by type
            summary.AlertsByType = allAlerts
                .GroupBy(a => a.AlertType)
                .ToDictionary(g => g.Key, g => g.Count());

            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting device alert summary");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Acknowledge a device alert
    /// </summary>
    /// <param name="alertId">Alert identifier</param>
    /// <returns>Success response</returns>
    [HttpPost("{alertId}/acknowledge")]
    public async Task<ActionResult> AcknowledgeDeviceAlert([Required] Guid alertId)
    {
        try
        {
            if (alertId == Guid.Empty)
            {
                return BadRequest("Invalid alert ID");
            }

            // Alert ownership validation is handled through the alert engine

            await _alertEngine.AcknowledgeDeviceAlertAsync(alertId, HttpContext.RequestAborted);

            _logger.LogInformation("Device alert {AlertId} acknowledged", alertId);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acknowledging device alert {AlertId}", alertId);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Test device alert generation for a specific device
    /// </summary>
    /// <param name="deviceId">Device identifier</param>
    /// <returns>List of generated alerts</returns>
    [HttpPost("test/{deviceId}")]
    public async Task<ActionResult<List<DeviceAlert>>> TestDeviceAlerts([Required] string deviceId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return BadRequest("Device ID is required");
            }

            var device = await _deviceRegistryService.GetDeviceAsync(
                deviceId,
                HttpContext.RequestAborted
            );
            if (device == null)
            {
                return NotFound($"Device {deviceId} not found");
            }

            var userId = HttpContext.GetSubjectIdString();
            if (device.UserId != userId && !HttpContext.IsAdmin())
            {
                return Forbid();
            }

            var alerts = await _alertEngine.ProcessDeviceAlertsAsync(
                device,
                HttpContext.RequestAborted
            );

            _logger.LogInformation(
                "Generated {AlertCount} test alerts for device {DeviceId}",
                alerts.Count,
                deviceId
            );

            return Ok(alerts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating test alerts for device {DeviceId}", deviceId);
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get alert settings/preferences for device alerts
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Alert settings</returns>
    [HttpGet("settings")]
    public async Task<ActionResult<DeviceAlertSettings>> GetAlertSettings(
        CancellationToken cancellationToken
    )
    {
        try
        {
            var userId = HttpContext.GetSubjectIdString();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            var prefs = await _preferencesRepository.GetPreferencesForUserAsync(
                userId,
                cancellationToken
            );

            var settings = new DeviceAlertSettings
            {
                EmailEnabled = prefs?.EmailEnabled ?? true,
                PushEnabled = prefs?.PushEnabled ?? true,
                SmsEnabled = prefs?.SmsEnabled ?? false,
                QuietHoursEnabled = prefs?.QuietHoursEnabled ?? true,
                QuietHoursStart = prefs?.QuietHoursStart?.ToTimeSpan() ?? new TimeSpan(22, 0, 0),
                QuietHoursEnd = prefs?.QuietHoursEnd?.ToTimeSpan() ?? new TimeSpan(6, 0, 0),
                CriticalAlertsOverrideQuietHours = prefs?.EmergencyOverrideQuietHours ?? true,
                BatteryLowThreshold = prefs?.BatteryLowThreshold ?? 20,
                SensorExpirationWarningHours = prefs?.SensorExpirationWarningHours ?? 24,
                DataGapWarningMinutes = prefs?.DataGapWarningMinutes ?? 30,
                CalibrationReminderHours = prefs?.CalibrationReminderHours ?? 12,
            };

            return Ok(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting alert settings");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Update alert settings/preferences for device alerts
    /// </summary>
    /// <param name="settings">Alert settings</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success response</returns>
    [HttpPut("settings")]
    public async Task<ActionResult> UpdateAlertSettings(
        [FromBody] DeviceAlertSettings settings,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var userId = HttpContext.GetSubjectIdString();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            var prefs =
                await _preferencesRepository.GetPreferencesForUserAsync(
                    userId,
                    cancellationToken
                )
                ?? new NotificationPreferencesEntity { UserId = userId };

            prefs.EmailEnabled = settings.EmailEnabled;
            prefs.PushEnabled = settings.PushEnabled;
            prefs.SmsEnabled = settings.SmsEnabled;
            prefs.QuietHoursEnabled = settings.QuietHoursEnabled;
            prefs.QuietHoursStart = TimeOnly.FromTimeSpan(settings.QuietHoursStart);
            prefs.QuietHoursEnd = TimeOnly.FromTimeSpan(settings.QuietHoursEnd);
            prefs.EmergencyOverrideQuietHours = settings.CriticalAlertsOverrideQuietHours;
            prefs.BatteryLowThreshold = settings.BatteryLowThreshold;
            prefs.SensorExpirationWarningHours = settings.SensorExpirationWarningHours;
            prefs.DataGapWarningMinutes = settings.DataGapWarningMinutes;
            prefs.CalibrationReminderHours = settings.CalibrationReminderHours;

            await _preferencesRepository.UpsertPreferencesAsync(prefs, cancellationToken);

            _logger.LogInformation("Alert settings updated for user {UserId}", userId);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating alert settings");
            return StatusCode(500, "Internal server error");
        }
    }
}

/// <summary>
/// Device alert summary model
/// </summary>
public class DeviceAlertSummary
{
    /// <summary>
    /// Total number of registered devices
    /// </summary>
    public int TotalDevices { get; set; }

    /// <summary>
    /// Number of active alerts
    /// </summary>
    public int ActiveAlerts { get; set; }

    /// <summary>
    /// Number of critical alerts
    /// </summary>
    public int CriticalAlerts { get; set; }

    /// <summary>
    /// Number of warning alerts
    /// </summary>
    public int WarningAlerts { get; set; }

    /// <summary>
    /// Alerts grouped by type
    /// </summary>
    public Dictionary<DeviceAlertType, int> AlertsByType { get; set; } = new();
}

/// <summary>
/// Device alert settings model
/// </summary>
public class DeviceAlertSettings
{
    /// <summary>
    /// Enable email notifications
    /// </summary>
    public bool EmailEnabled { get; set; } = true;

    /// <summary>
    /// Enable push notifications
    /// </summary>
    public bool PushEnabled { get; set; } = true;

    /// <summary>
    /// Enable SMS notifications
    /// </summary>
    public bool SmsEnabled { get; set; } = false;

    /// <summary>
    /// Enable quiet hours
    /// </summary>
    public bool QuietHoursEnabled { get; set; } = true;

    /// <summary>
    /// Quiet hours start time
    /// </summary>
    public TimeSpan QuietHoursStart { get; set; } = new TimeSpan(22, 0, 0);

    /// <summary>
    /// Quiet hours end time
    /// </summary>
    public TimeSpan QuietHoursEnd { get; set; } = new TimeSpan(6, 0, 0);

    /// <summary>
    /// Critical alerts override quiet hours
    /// </summary>
    public bool CriticalAlertsOverrideQuietHours { get; set; } = true;

    /// <summary>
    /// Battery low threshold percentage
    /// </summary>
    public int BatteryLowThreshold { get; set; } = 20;

    /// <summary>
    /// Sensor expiration warning hours
    /// </summary>
    public int SensorExpirationWarningHours { get; set; } = 24;

    /// <summary>
    /// Data gap warning minutes
    /// </summary>
    public int DataGapWarningMinutes { get; set; } = 30;

    /// <summary>
    /// Calibration reminder hours
    /// </summary>
    public int CalibrationReminderHours { get; set; } = 12;
}
