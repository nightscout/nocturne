using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services;

/// <summary>
/// Service for device alert engine and smart alerting
/// </summary>
public class DeviceAlertEngine : IDeviceAlertEngine
{
    private readonly NocturneDbContext _dbContext;
    private readonly NotificationPreferencesRepository _notificationPreferencesRepository;
    private readonly INotifierDispatcher _notifierDispatcher;
    private readonly ILogger<DeviceAlertEngine> _logger;
    private readonly DeviceHealthOptions _options;


    /// <summary>
    /// Initializes a new instance of the DeviceAlertEngine
    /// </summary>
    /// <param name="dbContext">Database context</param>
    /// <param name="notificationPreferencesRepository">Notification preferences repository</param>
    /// <param name="notifierDispatcher">Notification dispatcher</param>
    /// <param name="logger">Logger</param>
    /// <param name="options">Device health options</param>

    public DeviceAlertEngine(
        NocturneDbContext dbContext,
        NotificationPreferencesRepository notificationPreferencesRepository,
        INotifierDispatcher notifierDispatcher,
        ILogger<DeviceAlertEngine> logger,
        IOptions<DeviceHealthOptions> options

    )
    {
        _dbContext = dbContext;
        _notificationPreferencesRepository = notificationPreferencesRepository;
        _notifierDispatcher = notifierDispatcher;
        _logger = logger;
        _options = options.Value;

    }

    /// <summary>
    /// Process device health and generate appropriate alerts
    /// </summary>
    /// <param name="device">Device health entity</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of generated device alerts</returns>
    public async Task<List<DeviceAlert>> ProcessDeviceAlertsAsync(
        DeviceHealth device,
        CancellationToken cancellationToken
    )
    {
        if (device == null)
            throw new ArgumentNullException(nameof(device));

        _logger.LogDebug("Processing device alerts for device {DeviceId}", device.DeviceId);

        var alerts = new List<DeviceAlert>();

        // Generate battery alerts
        var batteryAlerts = await GenerateBatteryAlertsAsync(device, cancellationToken);
        alerts.AddRange(batteryAlerts);

        // Generate sensor expiration alerts (for CGM devices)
        if (device.DeviceType == DeviceType.CGM)
        {
            var sensorAlerts = await GenerateSensorExpirationAlertsAsync(device, cancellationToken);
            alerts.AddRange(sensorAlerts);
        }

        // Generate calibration alerts
        var calibrationAlerts = await GenerateCalibrationAlertsAsync(device, cancellationToken);
        alerts.AddRange(calibrationAlerts);

        // Generate data gap alerts
        var dataGapAlerts = await GenerateDataGapAlertsAsync(device, cancellationToken);
        alerts.AddRange(dataGapAlerts);

        // Generate device error alerts
        var errorAlerts = await GenerateDeviceErrorAlertsAsync(device, cancellationToken);
        alerts.AddRange(errorAlerts);

        // Filter alerts based on cooldown and escalation rules
        var filteredAlerts = new List<DeviceAlert>();
        foreach (var alert in alerts)
        {
            if (await ShouldSendAlertAsync(device.DeviceId, alert.AlertType, cancellationToken))
            {
                filteredAlerts.Add(alert);
            }
        }

        _logger.LogDebug(
            "Generated {AlertCount} alerts for device {DeviceId}",
            filteredAlerts.Count,
            device.DeviceId
        );

        return filteredAlerts;
    }

    /// <summary>
    /// Check if an alert should be sent based on cooldown and escalation rules
    /// </summary>
    /// <param name="deviceId">Device identifier</param>
    /// <param name="alertType">Type of alert</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if alert should be sent</returns>
    public async Task<bool> ShouldSendAlertAsync(
        string deviceId,
        DeviceAlertType alertType,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("Device ID cannot be null or empty", nameof(deviceId));

        _logger.LogDebug(
            "Checking if alert {AlertType} should be sent for device {DeviceId}",
            alertType,
            deviceId
        );

        // Get the device to check last maintenance alert time
        var deviceEntity = await _dbContext.DeviceHealth.FirstOrDefaultAsync(
            d => d.DeviceId == deviceId,
            cancellationToken
        );

        if (deviceEntity == null)
        {
            _logger.LogWarning(
                "Device {DeviceId} not found when checking alert cooldown",
                deviceId
            );
            return false;
        }

        // Check cooldown period
        if (deviceEntity.LastMaintenanceAlert.HasValue)
        {
            var hoursSinceLastAlert = (
                DateTime.UtcNow - deviceEntity.LastMaintenanceAlert.Value
            ).TotalHours;
            if (hoursSinceLastAlert < _options.MaintenanceAlertCooldownHours)
            {
                _logger.LogDebug(
                    "Alert {AlertType} for device {DeviceId} is in cooldown period",
                    alertType,
                    deviceId
                );
                return false;
            }
        }

        // Check if this is a critical alert (always send critical alerts)
        if (IsCriticalAlert(alertType))
        {
            _logger.LogDebug(
                "Alert {AlertType} for device {DeviceId} is critical, sending immediately",
                alertType,
                deviceId
            );
            return true;
        }

        // Check quiet hours (only for non-critical alerts)
        var inQuietHours = await _notificationPreferencesRepository.IsUserInQuietHoursAsync(
            deviceEntity.UserId,
            DateTime.UtcNow,
            cancellationToken
        );
        if (inQuietHours && !IsCriticalAlert(alertType))
        {
            _logger.LogDebug(
                "Alert {AlertType} for device {DeviceId} suppressed due to quiet hours",
                alertType,
                deviceId
            );
            return false;
        }

        return true;
    }

    /// <summary>
    /// Send device alert through appropriate notification channels
    /// </summary>
    /// <param name="deviceAlert">Device alert to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task completion</returns>
    public async Task SendDeviceAlertAsync(
        DeviceAlert deviceAlert,
        CancellationToken cancellationToken
    )
    {
        if (deviceAlert == null)
            throw new ArgumentNullException(nameof(deviceAlert));

        _logger.LogInformation(
            "Sending device alert {AlertType} for device {DeviceId}",
            deviceAlert.AlertType,
            deviceAlert.DeviceId
        );

        try
        {
            // Create alert rule event
            var notification = CreateNotificationFromDeviceAlert(deviceAlert);
            await DispatchNotificationAsync(
                notification,
                deviceAlert.Severity,
                deviceAlert.UserId
            );

            // Update the device's last maintenance alert time
            var deviceEntity = await _dbContext.DeviceHealth.FirstOrDefaultAsync(
                d => d.DeviceId == deviceAlert.DeviceId,
                cancellationToken
            );

            if (deviceEntity != null)
            {
                deviceEntity.LastMaintenanceAlert = DateTime.UtcNow;
                deviceEntity.UpdatedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            _logger.LogDebug(
                "Successfully sent device alert {AlertType} for device {DeviceId}",
                deviceAlert.AlertType,
                deviceAlert.DeviceId
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to send device alert {AlertType} for device {DeviceId}",
                deviceAlert.AlertType,
                deviceAlert.DeviceId
            );
            throw;
        }
    }

    /// <summary>
    /// Acknowledge a device alert
    /// </summary>
    /// <param name="alertId">Alert identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task completion</returns>
    public async Task AcknowledgeDeviceAlertAsync(Guid alertId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Acknowledging device alert {AlertId}", alertId);

        var clearNotification = new NotificationBase
        {
            Level = 0,
            Title = "Device alert cleared",
            Message = $"Device alert {alertId} acknowledged",
            Group = "device-alerts",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Clear = true,
        };

        await _notifierDispatcher.DispatchAsync(
            clearNotification,
            "system",
            cancellationToken
        );

        _logger.LogInformation("Device alert {AlertId} acknowledged", alertId);
    }

    #region Private Helper Methods

    /// <summary>
    /// Generate battery alerts for a device
    /// </summary>
    private Task<List<DeviceAlert>> GenerateBatteryAlertsAsync(
        DeviceHealth device,
        CancellationToken cancellationToken
    )
    {
        var alerts = new List<DeviceAlert>();

        if (!device.BatteryLevel.HasValue)
            return Task.FromResult(alerts);

        var batteryLevel = device.BatteryLevel.Value;

        // Critical battery alert (< 10%)
        if (batteryLevel < 10)
        {
            alerts.Add(
                new DeviceAlert
                {
                    DeviceId = device.DeviceId,
                    UserId = device.UserId,
                    AlertType = DeviceAlertType.BatteryCritical,
                    Severity = DeviceIssueSeverity.Critical,
                    Title = "Critical Battery Level",
                    Message =
                        $"Device {device.DeviceName} battery is critically low at {batteryLevel}%. Immediate action required.",
                    Data = new Dictionary<string, object>
                    {
                        ["batteryLevel"] = batteryLevel,
                        ["deviceType"] = device.DeviceType.ToString(),
                    },
                }
            );
        }
        // Battery warning alert (< threshold)
        else if (batteryLevel <= device.BatteryWarningThreshold)
        {
            alerts.Add(
                new DeviceAlert
                {
                    DeviceId = device.DeviceId,
                    UserId = device.UserId,
                    AlertType = DeviceAlertType.BatteryWarning,
                    Severity = DeviceIssueSeverity.High,
                    Title = "Low Battery Warning",
                    Message =
                        $"Device {device.DeviceName} battery is low at {batteryLevel}%. Consider charging or replacing soon.",
                    Data = new Dictionary<string, object>
                    {
                        ["batteryLevel"] = batteryLevel,
                        ["deviceType"] = device.DeviceType.ToString(),
                    },
                }
            );
        }

        return Task.FromResult(alerts);
    }

    /// <summary>
    /// Generate sensor expiration alerts for CGM devices
    /// </summary>
    private Task<List<DeviceAlert>> GenerateSensorExpirationAlertsAsync(
        DeviceHealth device,
        CancellationToken cancellationToken
    )
    {
        var alerts = new List<DeviceAlert>();

        if (!device.SensorExpiration.HasValue)
            return Task.FromResult(alerts);

        var hoursUntilExpiration = (device.SensorExpiration.Value - DateTime.UtcNow).TotalHours;

        // Sensor expired
        if (hoursUntilExpiration <= 0)
        {
            alerts.Add(
                new DeviceAlert
                {
                    DeviceId = device.DeviceId,
                    UserId = device.UserId,
                    AlertType = DeviceAlertType.SensorExpired,
                    Severity = DeviceIssueSeverity.Critical,
                    Title = "Sensor Expired",
                    Message =
                        $"CGM sensor for {device.DeviceName} has expired. Replace immediately for continued monitoring.",
                    Data = new Dictionary<string, object>
                    {
                        ["sensorExpiration"] = device.SensorExpiration.Value,
                        ["hoursOverdue"] = Math.Abs(hoursUntilExpiration),
                    },
                }
            );
        }
        // Sensor expiring soon
        else if (hoursUntilExpiration <= device.SensorExpirationWarningHours)
        {
            var severity =
                hoursUntilExpiration <= 6 ? DeviceIssueSeverity.High : DeviceIssueSeverity.Medium;
            alerts.Add(
                new DeviceAlert
                {
                    DeviceId = device.DeviceId,
                    UserId = device.UserId,
                    AlertType = DeviceAlertType.SensorExpirationWarning,
                    Severity = severity,
                    Title = "Sensor Expiring Soon",
                    Message =
                        $"CGM sensor for {device.DeviceName} expires in {hoursUntilExpiration:F1} hours. Prepare for replacement.",
                    Data = new Dictionary<string, object>
                    {
                        ["sensorExpiration"] = device.SensorExpiration.Value,
                        ["hoursRemaining"] = hoursUntilExpiration,
                    },
                }
            );
        }

        return Task.FromResult(alerts);
    }

    /// <summary>
    /// Generate calibration alerts for devices
    /// </summary>
    private Task<List<DeviceAlert>> GenerateCalibrationAlertsAsync(
        DeviceHealth device,
        CancellationToken cancellationToken
    )
    {
        var alerts = new List<DeviceAlert>();

        // Only generate calibration alerts for devices that require calibration
        if (device.DeviceType != DeviceType.CGM && device.DeviceType != DeviceType.BGM)
            return Task.FromResult(alerts);

        if (!device.LastCalibration.HasValue)
        {
            // Device has never been calibrated
            alerts.Add(
                new DeviceAlert
                {
                    DeviceId = device.DeviceId,
                    UserId = device.UserId,
                    AlertType = DeviceAlertType.CalibrationReminder,
                    Severity = DeviceIssueSeverity.Medium,
                    Title = "Calibration Required",
                    Message =
                        $"Device {device.DeviceName} requires initial calibration for accurate readings.",
                    Data = new Dictionary<string, object>
                    {
                        ["deviceType"] = device.DeviceType.ToString(),
                        ["initialCalibration"] = true,
                    },
                }
            );
            return Task.FromResult(alerts);
        }

        var hoursSinceCalibration = (DateTime.UtcNow - device.LastCalibration.Value).TotalHours;

        // Calibration overdue
        if (hoursSinceCalibration > device.CalibrationReminderHours * 2)
        {
            alerts.Add(
                new DeviceAlert
                {
                    DeviceId = device.DeviceId,
                    UserId = device.UserId,
                    AlertType = DeviceAlertType.CalibrationOverdue,
                    Severity = DeviceIssueSeverity.High,
                    Title = "Calibration Overdue",
                    Message =
                        $"Device {device.DeviceName} calibration is overdue by {hoursSinceCalibration - device.CalibrationReminderHours:F1} hours.",
                    Data = new Dictionary<string, object>
                    {
                        ["lastCalibration"] = device.LastCalibration.Value,
                        ["hoursOverdue"] = hoursSinceCalibration - device.CalibrationReminderHours,
                    },
                }
            );
        }
        // Calibration reminder
        else if (hoursSinceCalibration >= device.CalibrationReminderHours)
        {
            alerts.Add(
                new DeviceAlert
                {
                    DeviceId = device.DeviceId,
                    UserId = device.UserId,
                    AlertType = DeviceAlertType.CalibrationReminder,
                    Severity = DeviceIssueSeverity.Medium,
                    Title = "Calibration Reminder",
                    Message =
                        $"Device {device.DeviceName} is due for calibration. Last calibrated {hoursSinceCalibration:F1} hours ago.",
                    Data = new Dictionary<string, object>
                    {
                        ["lastCalibration"] = device.LastCalibration.Value,
                        ["hoursSinceCalibration"] = hoursSinceCalibration,
                    },
                }
            );
        }

        return Task.FromResult(alerts);
    }

    /// <summary>
    /// Generate data gap alerts for devices
    /// </summary>
    private Task<List<DeviceAlert>> GenerateDataGapAlertsAsync(
        DeviceHealth device,
        CancellationToken cancellationToken
    )
    {
        var alerts = new List<DeviceAlert>();

        if (!device.LastDataReceived.HasValue)
        {
            // No data ever received
            alerts.Add(
                new DeviceAlert
                {
                    DeviceId = device.DeviceId,
                    UserId = device.UserId,
                    AlertType = DeviceAlertType.CommunicationFailure,
                    Severity = DeviceIssueSeverity.High,
                    Title = "No Data Received",
                    Message =
                        $"No data has been received from device {device.DeviceName}. Check device connectivity.",
                    Data = new Dictionary<string, object>
                    {
                        ["deviceType"] = device.DeviceType.ToString(),
                        ["neverReceived"] = true,
                    },
                }
            );
            return Task.FromResult(alerts);
        }

        var minutesSinceLastData = (DateTime.UtcNow - device.LastDataReceived.Value).TotalMinutes;

        if (minutesSinceLastData > device.DataGapWarningMinutes)
        {
            var severity =
                minutesSinceLastData > 120 ? DeviceIssueSeverity.High : DeviceIssueSeverity.Medium;
            alerts.Add(
                new DeviceAlert
                {
                    DeviceId = device.DeviceId,
                    UserId = device.UserId,
                    AlertType = DeviceAlertType.DataGapDetected,
                    Severity = severity,
                    Title = "Data Gap Detected",
                    Message =
                        $"No data received from {device.DeviceName} for {minutesSinceLastData:F0} minutes. Check device status.",
                    Data = new Dictionary<string, object>
                    {
                        ["lastDataReceived"] = device.LastDataReceived.Value,
                        ["minutesSinceLastData"] = minutesSinceLastData,
                    },
                }
            );
        }

        return Task.FromResult(alerts);
    }

    /// <summary>
    /// Generate device error alerts
    /// </summary>
    private Task<List<DeviceAlert>> GenerateDeviceErrorAlertsAsync(
        DeviceHealth device,
        CancellationToken cancellationToken
    )
    {
        var alerts = new List<DeviceAlert>();

        if (device.Status == DeviceStatusType.Error || device.Status == DeviceStatusType.Warning)
        {
            var severity =
                device.Status == DeviceStatusType.Error
                    ? DeviceIssueSeverity.Critical
                    : DeviceIssueSeverity.Medium;
            var data = new Dictionary<string, object>
            {
                ["deviceStatus"] = device.Status.ToString(),
                ["lastErrorMessage"] = device.LastErrorMessage ?? string.Empty,
            };

            if (device.LastStatusUpdate.HasValue)
            {
                data["lastStatusUpdate"] = device.LastStatusUpdate.Value;
            }

            alerts.Add(
                new DeviceAlert
                {
                    DeviceId = device.DeviceId,
                    UserId = device.UserId,
                    AlertType = DeviceAlertType.DeviceError,
                    Severity = severity,
                    Title =
                        device.Status == DeviceStatusType.Error ? "Device Error" : "Device Warning",
                    Message =
                        device.LastErrorMessage
                        ?? $"Device {device.DeviceName} is reporting a {device.Status.ToString().ToLower()}.",
                    Data = data,
                }
            );
        }

        return Task.FromResult(alerts);
    }

    /// <summary>
    /// Check if an alert type is critical (should always be sent)
    /// </summary>
    private static bool IsCriticalAlert(DeviceAlertType alertType)
    {
        return alertType switch
        {
            DeviceAlertType.BatteryCritical => true,
            DeviceAlertType.SensorExpired => true,
            DeviceAlertType.DeviceError => true,
            DeviceAlertType.CommunicationFailure => true,
            _ => false,
        };
    }

    /// <summary>
    /// Map device alert to NotificationBase for broadcast
    /// </summary>
    private NotificationBase CreateNotificationFromDeviceAlert(DeviceAlert deviceAlert)
    {
        var level = deviceAlert.Severity switch
        {
            DeviceIssueSeverity.Critical => 2,
            DeviceIssueSeverity.High => 1,
            DeviceIssueSeverity.Medium => 1,
            _ => 0,
        };

        return new NotificationBase
        {
            Level = level,
            Title = deviceAlert.Title,
            Message = deviceAlert.Message,
            Group = "device-alerts",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Plugin = "device-health",
            IsAnnouncement = false,
            Debug = new
            {
                deviceAlert.Id,
                deviceAlert.DeviceId,
                AlertType = deviceAlert.AlertType.ToString(),
                Severity = deviceAlert.Severity.ToString(),
            },
        };
    }

    /// <summary>
    /// Dispatch notification through configured notifiers using severity mapping
    /// </summary>
    private Task DispatchNotificationAsync(
        NotificationBase notification,
        DeviceIssueSeverity severity,
        string userId
    )
    {
        notification.Level = severity switch
        {
            DeviceIssueSeverity.Critical => 2,
            DeviceIssueSeverity.High => 1,
            DeviceIssueSeverity.Medium => 1,
            _ => 0,
        };

        return _notifierDispatcher.DispatchAsync(notification, userId);
    }

    #endregion
}
