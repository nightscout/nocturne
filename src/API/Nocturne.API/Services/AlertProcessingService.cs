using System.Text.Json;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services;

/// <summary>
/// Service for processing alert events and managing alert lifecycle
/// </summary>
public class AlertProcessingService : IAlertProcessingService
{
    private readonly AlertHistoryRepository _alertHistoryRepository;
    private readonly AlertRuleRepository _alertRuleRepository;
    private readonly NotificationPreferencesRepository _notificationPreferencesRepository;
    private readonly INotifierDispatcher _notifierDispatcher;
    private readonly AlertMonitoringOptions _options;
    private readonly ILogger<AlertProcessingService> _logger;

    public AlertProcessingService(
        AlertHistoryRepository alertHistoryRepository,
        AlertRuleRepository alertRuleRepository,
        NotificationPreferencesRepository notificationPreferencesRepository,
        INotifierDispatcher notifierDispatcher,
        IOptions<AlertMonitoringOptions> options,
        ILogger<AlertProcessingService> logger
    )
    {
        _alertHistoryRepository = alertHistoryRepository;
        _alertRuleRepository = alertRuleRepository;
        _notificationPreferencesRepository = notificationPreferencesRepository;
        _notifierDispatcher = notifierDispatcher;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ProcessAlertEvent(AlertEvent alertEvent, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "Processing {AlertType} alert for user {UserId} with glucose {GlucoseValue} mg/dL",
                alertEvent.AlertType,
                alertEvent.UserId,
                alertEvent.GlucoseValue
            );

            // Create alert history entry
            var alertHistory = await CreateAlertHistoryEntry(alertEvent, cancellationToken);

            // Send real-time notification via SignalR
            await SendNotificationAsync(alertEvent, alertHistory, cancellationToken);

            // Resolve conflicting alerts if this is a resolution alert
            await ResolveConflictingAlerts(alertEvent, cancellationToken);

            _logger.LogInformation(
                "Successfully processed alert {AlertId} for user {UserId}",
                alertHistory.Id,
                alertEvent.UserId
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error processing alert event for user {UserId}",
                alertEvent.UserId
            );
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ResolveAlert(Guid alertId, CancellationToken cancellationToken)
    {
        var alert = await _alertHistoryRepository.UpdateAlertStatusAsync(
            alertId,
            "RESOLVED",
            resolvedAt: DateTime.UtcNow,
            cancellationToken: cancellationToken
        );

        if (alert != null)
        {
            _logger.LogInformation(
                "Resolved alert {AlertId} for user {UserId}",
                alertId,
                alert.UserId
            );

            // Send clear alarm notification
            await SendClearAlarmNotification(alert, cancellationToken);
        }
        else
        {
            _logger.LogWarning("Alert {AlertId} not found for resolution", alertId);
        }
    }

    /// <inheritdoc />
    public async Task AcknowledgeAlert(
        Guid alertId,
        int snoozeMinutes,
        CancellationToken cancellationToken
    )
    {
        var status = snoozeMinutes > 0 ? "SNOOZED" : "ACKNOWLEDGED";
        var snoozeUntil =
            snoozeMinutes > 0 ? DateTime.UtcNow.AddMinutes(snoozeMinutes) : (DateTime?)null;

        var alert = await _alertHistoryRepository.UpdateAlertStatusAsync(
            alertId,
            status,
            acknowledgedAt: DateTime.UtcNow,
            snoozeUntil: snoozeUntil,
            cancellationToken: cancellationToken
        );

        if (alert != null)
        {
            _logger.LogInformation(
                "Acknowledged alert {AlertId} for user {UserId} with {SnoozeMinutes} minute snooze",
                alertId,
                alert.UserId,
                snoozeMinutes
            );
        }
        else
        {
            _logger.LogWarning("Alert {AlertId} not found for acknowledgment", alertId);
        }
    }

    /// <inheritdoc />
    public async Task ResolveAlertsForUser(
        string userId,
        AlertType alertType,
        CancellationToken cancellationToken
    )
    {
        var resolvedCount = await _alertHistoryRepository.ResolveAlertsAsync(
            userId,
            alertType.ToString(),
            cancellationToken
        );

        if (resolvedCount > 0)
        {
            _logger.LogInformation(
                "Resolved {Count} {AlertType} alerts for user {UserId}",
                resolvedCount,
                alertType,
                userId
            );
        }
    }

    /// <inheritdoc />
    public async Task ProcessAlertEscalations(CancellationToken cancellationToken)
    {
        try
        {
            // Get all active alerts that might need escalation

            var alerts = await _alertHistoryRepository.GetAlertsForEscalationAsync();

            if (alerts.Count == 0)
            {
                _logger.LogDebug("No alerts pending escalation");
                return;
            }
            _logger.LogDebug("Processing alert escalations");

            var now = DateTime.UtcNow;
            var alertsToProcess = alerts.Take(_options.AlertProcessingBatchSize).ToList();

            foreach (var alert in alertsToProcess)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.Equals(alert.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug(
                        "Skipping alert {AlertId} with status {Status}",
                        alert.Id,
                        alert.Status
                    );
                    continue;
                }

                var rule =
                    alert.AlertRule
                    ?? (
                        alert.AlertRuleId.HasValue
                            ? await _alertRuleRepository.GetRuleByIdAsync(
                                alert.AlertRuleId.Value,
                                cancellationToken
                            )
                            : null
                    );

                var escalationDelayMinutes =
                    rule?.EscalationDelayMinutes ?? _options.AlertCooldownMinutes;
                var maxEscalations = rule?.MaxEscalations ?? 3;

                if (alert.NextEscalationTime == null)
                {
                    alert.NextEscalationTime = alert.TriggerTime.AddMinutes(escalationDelayMinutes);
                    alert.EscalationReason ??= "Initial escalation scheduled";
                    await _alertHistoryRepository.UpdateAsync(alert);
                    _logger.LogDebug(
                        "Scheduled first escalation for alert {AlertId} at {NextEscalationTime}",
                        alert.Id,
                        alert.NextEscalationTime
                    );
                    continue;
                }

                if (alert.NextEscalationTime > now)
                {
                    continue; // Not yet time to escalate
                }

                if (alert.EscalationLevel >= maxEscalations)
                {
                    alert.NextEscalationTime = null;
                    alert.EscalationReason = "Reached maximum escalations";
                    await _alertHistoryRepository.UpdateAsync(alert);
                    _logger.LogInformation(
                        "Alert {AlertId} for user {UserId} reached max escalations ({Max})",
                        alert.Id,
                        alert.UserId,
                        maxEscalations
                    );
                    continue;
                }

                var isUrgent =
                    string.Equals(
                        alert.AlertType,
                        AlertType.UrgentLow.ToString(),
                        StringComparison.OrdinalIgnoreCase
                    )
                    || string.Equals(
                        alert.AlertType,
                        AlertType.UrgentHigh.ToString(),
                        StringComparison.OrdinalIgnoreCase
                    );

                var inQuietHours = await _notificationPreferencesRepository.IsUserInQuietHoursAsync(
                    alert.UserId,
                    now,
                    cancellationToken
                );

                if (inQuietHours && !isUrgent)
                {
                    alert.NextEscalationTime = now.AddMinutes(escalationDelayMinutes);
                    alert.EscalationReason = "Escalation deferred due to quiet hours";
                    await _alertHistoryRepository.UpdateAsync(alert);
                    _logger.LogDebug(
                        "Deferring escalation for alert {AlertId} until {NextEscalationTime} due to quiet hours",
                        alert.Id,
                        alert.NextEscalationTime
                    );
                    continue;
                }

                var newEscalationLevel = alert.EscalationLevel + 1;

                // Send another notification to escalate
                var alertEvent = CreateAlertEventFromHistory(alert);
                if (alertEvent != null)
                {
                    await SendNotificationAsync(alertEvent, alert, cancellationToken);
                }

                // Record escalation attempt
                var attempts = DeserializeEscalationAttempts(alert.EscalationAttempts);
                attempts.Add(
                    new EscalationAttempt
                    {
                        Level = newEscalationLevel,
                        AttemptTime = now,
                        ChannelsUsed = new List<string> { "SignalR" },
                        Success = true,
                    }
                );

                alert.EscalationLevel = newEscalationLevel;
                alert.NextEscalationTime = now.AddMinutes(escalationDelayMinutes);
                alert.EscalationAttempts = JsonSerializer.Serialize(attempts);
                alert.EscalationReason = $"Escalation level {newEscalationLevel} triggered";

                await _alertHistoryRepository.UpdateAsync(alert);

                _logger.LogInformation(
                    "Escalated alert {AlertId} for user {UserId} to level {Level}",
                    alert.Id,
                    alert.UserId,
                    newEscalationLevel
                );
            }

            _logger.LogDebug("Completed processing alert escalations");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing alert escalations");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task CleanupOldAlerts(
        int daysToKeep = 30,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var deletedCount = await _alertHistoryRepository.CleanupOldAlertsAsync(
                daysToKeep,
                cancellationToken
            );

            _logger.LogInformation(
                "Cleaned up {DeletedCount} old alert records older than {DaysToKeep} days",
                deletedCount,
                daysToKeep
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old alerts");
            throw;
        }
    }

    private async Task<AlertHistoryEntity> CreateAlertHistoryEntry(
        AlertEvent alertEvent,
        CancellationToken cancellationToken
    )
    {
        var alertHistory = new AlertHistoryEntity
        {
            UserId = alertEvent.UserId,
            AlertRuleId = alertEvent.AlertRuleId,
            AlertType = alertEvent.AlertType.ToString(),
            GlucoseValue = alertEvent.GlucoseValue,
            Threshold = alertEvent.Threshold,
            Status = "ACTIVE",
            TriggerTime = alertEvent.TriggerTime,
            EscalationLevel = 0,
            NotificationsSent = "[]",
        };

        return await _alertHistoryRepository.CreateAlertAsync(alertHistory, cancellationToken);
    }

    private async Task SendNotificationAsync(
        AlertEvent alertEvent,
        AlertHistoryEntity alertHistory,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var notification = CreateNotificationFromAlert(alertEvent, alertHistory);
            await _notifierDispatcher.DispatchAsync(
                notification,
                alertEvent.UserId,
                cancellationToken
            );

            _logger.LogDebug(
                "Sent SignalR notification for {AlertType} alert to user {UserId}",
                alertEvent.AlertType,
                alertEvent.UserId
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error sending SignalR notification for alert {AlertId}",
                alertHistory.Id
            );
            // Don't throw - notification failure shouldn't fail alert processing
        }
    }

    private async Task SendClearAlarmNotification(
        AlertHistoryEntity alert,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var clearNotification = new NotificationBase
            {
                Level = GetNotificationLevel(alert.AlertType),
                Title = $"{alert.AlertType} Alert Cleared",
                Message = $"Alert for {alert.AlertType} condition has been resolved",
                Group = "glucose-alerts",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Clear = true,
            };
            await _notifierDispatcher.DispatchAsync(
                clearNotification,
                alert.UserId,
                cancellationToken
            );

            _logger.LogDebug("Sent clear alarm notification for alert {AlertId}", alert.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error sending clear alarm notification for alert {AlertId}",
                alert.Id
            );
        }
    }

    private NotificationBase CreateNotificationFromAlert(
        AlertEvent alertEvent,
        AlertHistoryEntity alertHistory
    )
    {
        var level = GetNotificationLevel(alertEvent.AlertType.ToString());
        var title = GetAlertTitle(alertEvent.AlertType);
        var message = GetAlertMessage(alertEvent);

        return new NotificationBase
        {
            Level = level,
            Title = title,
            Message = message,
            Group = "glucose-alerts",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Plugin = "glucose-monitoring",
            IsAnnouncement = false,
            Debug = new { AlertId = alertHistory.Id, RuleId = alertEvent.AlertRuleId },
            Count = 1,
            LastRecorded = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Persistent =
                alertEvent.AlertType == AlertType.UrgentLow
                || alertEvent.AlertType == AlertType.UrgentHigh,
        };
    }

    private int GetNotificationLevel(string alertType)
    {
        return alertType switch
        {
            "UrgentLow" or "UrgentHigh" => 2, // URGENT
            "Low" or "High" => 1, // WARN
            "DeviceWarning" => 1, // WARN
            _ => 0, // INFO
        };
    }

    private string GetAlertTitle(AlertType alertType)
    {
        return alertType switch
        {
            AlertType.UrgentLow => "URGENT: Low Glucose",
            AlertType.UrgentHigh => "URGENT: High Glucose",
            AlertType.Low => "Low Glucose",
            AlertType.High => "High Glucose",
            AlertType.DeviceWarning => "Device Warning",
            _ => "Glucose Alert",
        };
    }

    private string GetAlertMessage(AlertEvent alertEvent)
    {
        var direction = alertEvent.Context?.GetValueOrDefault("Direction", "")?.ToString() ?? "";
        var delta = alertEvent.Context?.GetValueOrDefault("Delta", 0)?.ToString() ?? "0";

        var directionText = !string.IsNullOrEmpty(direction) ? $" ({direction})" : "";
        var deltaText = delta != "0" ? $" Δ{delta}" : "";

        return alertEvent.AlertType switch
        {
            AlertType.UrgentLow =>
                $"Glucose is {alertEvent.GlucoseValue} mg/dL (below {alertEvent.Threshold}){directionText}{deltaText}",
            AlertType.UrgentHigh =>
                $"Glucose is {alertEvent.GlucoseValue} mg/dL (above {alertEvent.Threshold}){directionText}{deltaText}",
            AlertType.Low =>
                $"Glucose is {alertEvent.GlucoseValue} mg/dL (below {alertEvent.Threshold}){directionText}{deltaText}",
            AlertType.High =>
                $"Glucose is {alertEvent.GlucoseValue} mg/dL (above {alertEvent.Threshold}){directionText}{deltaText}",
            AlertType.DeviceWarning => $"Device warning detected",
            _ => $"Glucose level requires attention: {alertEvent.GlucoseValue} mg/dL",
        };
    }

    private async Task ResolveConflictingAlerts(
        AlertEvent alertEvent,
        CancellationToken cancellationToken
    )
    {
        // Resolve opposite type alerts when glucose returns to normal range
        try
        {
            if (
                alertEvent.AlertType == AlertType.High
                || alertEvent.AlertType == AlertType.UrgentHigh
            )
            {
                // If high alert, resolve any low alerts
                await ResolveAlertsForUser(alertEvent.UserId, AlertType.Low, cancellationToken);
                await ResolveAlertsForUser(
                    alertEvent.UserId,
                    AlertType.UrgentLow,
                    cancellationToken
                );
            }
            else if (
                alertEvent.AlertType == AlertType.Low
                || alertEvent.AlertType == AlertType.UrgentLow
            )
            {
                // If low alert, resolve any high alerts
                await ResolveAlertsForUser(alertEvent.UserId, AlertType.High, cancellationToken);
                await ResolveAlertsForUser(
                    alertEvent.UserId,
                    AlertType.UrgentHigh,
                    cancellationToken
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Error resolving conflicting alerts for user {UserId}",
                alertEvent.UserId
            );
            // Don't throw - this is a nice-to-have cleanup
        }
    }

    private static List<EscalationAttempt> DeserializeEscalationAttempts(string escalationAttempts)
    {
        try
        {
            return JsonSerializer.Deserialize<List<EscalationAttempt>>(escalationAttempts)
                ?? new List<EscalationAttempt>();
        }
        catch
        {
            return new List<EscalationAttempt>();
        }
    }

    private AlertEvent? CreateAlertEventFromHistory(AlertHistoryEntity alert)
    {
        if (!Enum.TryParse<AlertType>(alert.AlertType, out var alertType))
        {
            return null;
        }

        return new AlertEvent
        {
            UserId = alert.UserId,
            AlertRuleId = alert.AlertRuleId ?? Guid.Empty,
            AlertType = alertType,
            GlucoseValue = alert.GlucoseValue ?? 0,
            Threshold = alert.Threshold ?? 0,
            TriggerTime = alert.TriggerTime,
            Context = new Dictionary<string, object>
            {
                { "EscalationLevel", alert.EscalationLevel + 1 },
                { "IsEscalation", true },
            },
        };
    }
}
