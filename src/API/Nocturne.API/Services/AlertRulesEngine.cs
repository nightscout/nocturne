using System.Text.Json;
using Microsoft.Extensions.Options;
using Nocturne.API.Controllers.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services;

/// <summary>
/// Alert rules engine implementation that evaluates glucose thresholds and generates alerts
/// </summary>
public class AlertRulesEngine : IAlertRulesEngine
{
    private readonly AlertRuleRepository _alertRuleRepository;
    private readonly AlertHistoryRepository _alertHistoryRepository;
    private readonly NotificationPreferencesRepository _notificationPreferencesRepository;
    private readonly IPredictionService? _predictionService;
    private readonly AlertMonitoringOptions _options;
    private readonly ILogger<AlertRulesEngine> _logger;

    public AlertRulesEngine(
        AlertRuleRepository alertRuleRepository,
        AlertHistoryRepository alertHistoryRepository,
        NotificationPreferencesRepository notificationPreferencesRepository,
        IOptions<AlertMonitoringOptions> options,
        ILogger<AlertRulesEngine> logger,
        IPredictionService? predictionService = null
    )
    {
        _alertRuleRepository = alertRuleRepository;
        _alertHistoryRepository = alertHistoryRepository;
        _notificationPreferencesRepository = notificationPreferencesRepository;
        _predictionService = predictionService;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<AlertEvent>> EvaluateGlucoseData(
        Entry glucoseReading,
        string userId,
        CancellationToken cancellationToken
    )
    {
        var alertEvents = new List<AlertEvent>();

        try
        {
            // Get active rules for the user
            var activeRules = await GetActiveRulesForUser(userId, cancellationToken);

            if (activeRules.Length == 0)
            {
                _logger.LogDebug("No active alert rules found for user {UserId}", userId);
                return alertEvents;
            }

            // Check if user is in quiet hours
            var isInQuietHours = await IsUserInQuietHours(
                userId,
                glucoseReading.Date,
                cancellationToken
            );
            if (isInQuietHours)
            {
                _logger.LogDebug(
                    "User {UserId} is in quiet hours, skipping alert evaluation",
                    userId
                );
                return alertEvents;
            }

            // Check if we've hit the maximum active alerts for this user
            var activeAlertCount = await _alertHistoryRepository.GetActiveAlertCountForUserAsync(
                userId,
                cancellationToken
            );
            if (activeAlertCount >= _options.MaxActiveAlertsPerUser)
            {
                _logger.LogWarning(
                    "User {UserId} has reached maximum active alerts ({Count}), skipping new alert evaluation",
                    userId,
                    activeAlertCount
                );
                return alertEvents;
            }

            // Get predictions if potentially needed and prediction service is available
            GlucosePredictionResponse? predictions = null;
            if (
                _predictionService != null
                && activeRules.Any(r =>
                    r.ForecastLeadTimeMinutes.HasValue && r.ForecastLeadTimeMinutes > 0
                )
            )
            {
                try
                {
                    predictions = await _predictionService.GetPredictionsAsync(
                        null,
                        cancellationToken
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to get predictions for alert evaluation for user {UserId}",
                        userId
                    );
                }
            }

            // Evaluate each rule
            foreach (var rule in activeRules)
            {
                try
                {
                    // Check time-based conditions
                    if (!EvaluateTimeBasedConditions(rule, glucoseReading.Date ?? DateTime.UtcNow))
                    {
                        _logger.LogDebug(
                            "Time-based conditions not met for rule {RuleId} for user {UserId}",
                            rule.Id,
                            userId
                        );
                        continue;
                    }

                    // Check alert conditions
                    if (
                        await IsAlertConditionMetInternal(
                            glucoseReading,
                            rule,
                            cancellationToken,
                            predictions
                        )
                    )
                    {
                        var alertEvent = await CreateAlertEvent(
                            glucoseReading,
                            rule,
                            cancellationToken,
                            predictions
                        );
                        if (alertEvent != null)
                        {
                            alertEvents.Add(alertEvent);
                            _logger.LogInformation(
                                "Generated {AlertType} alert for user {UserId} with glucose {GlucoseValue} mg/dL",
                                alertEvent.AlertType,
                                userId,
                                alertEvent.GlucoseValue
                            );
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Error evaluating rule {RuleId} for user {UserId}",
                        rule.Id,
                        userId
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating glucose data for user {UserId}", userId);
        }

        return alertEvents;
    }

    /// <inheritdoc />
    public async Task<AlertRuleEntity[]> GetActiveRulesForUser(
        string userId,
        CancellationToken cancellationToken
    )
    {
        return await _alertRuleRepository.GetActiveRulesForUserAsync(userId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> IsAlertConditionMet(
        Entry glucoseReading,
        AlertRuleEntity rule,
        CancellationToken cancellationToken
    )
    {
        return await IsAlertConditionMetInternal(glucoseReading, rule, cancellationToken, null);
    }

    private async Task<bool> IsAlertConditionMetInternal(
        Entry glucoseReading,
        AlertRuleEntity rule,
        CancellationToken cancellationToken,
        GlucosePredictionResponse? predictions
    )
    {
        var glucoseValue = (decimal)(glucoseReading.Sgv ?? glucoseReading.Mgdl);

        // Check each threshold type
        // Tuple: (AlertType, Threshold, IsForecast)
        var alertTypes = new List<(AlertType Type, decimal? Threshold, bool IsForecast)>
        {
            (AlertType.UrgentLow, rule.UrgentLowThreshold, false),
            (AlertType.UrgentHigh, rule.UrgentHighThreshold, false),
            (AlertType.Low, rule.LowThreshold, false),
            (AlertType.High, rule.HighThreshold, false),
        };

        if (
            rule.ForecastLeadTimeMinutes.HasValue
            && rule.ForecastLeadTimeMinutes > 0
            && rule.LowThreshold.HasValue
        )
        {
            alertTypes.Add((AlertType.ForecastLow, rule.LowThreshold, true));
        }

        foreach (var check in alertTypes)
        {
            if (!check.Threshold.HasValue)
                continue;

            var thresholdValue = check.Threshold.Value;
            var isConditionMet = false;

            if (check.IsForecast && check.Type == AlertType.ForecastLow)
            {
                if (
                    predictions?.Predictions?.Default != null
                    && rule.ForecastLeadTimeMinutes.HasValue
                )
                {
                    // Calculate index for lead time (5 min intervals)
                    // Index 0 = NOW (T+0), index 1 = T+5min, index 2 = T+10min, etc.
                    // So 30 minutes = index 6 (30/5 = 6)
                    var targetIndex = rule.ForecastLeadTimeMinutes.Value / 5;

                    if (targetIndex < predictions.Predictions.Default.Count)
                    {
                        var predictedValue = (decimal)predictions.Predictions.Default[targetIndex];
                        isConditionMet = predictedValue <= thresholdValue;
                    }
                }
            }
            else
            {
                isConditionMet = check.Type switch
                {
                    AlertType.Low or AlertType.UrgentLow => glucoseValue <= thresholdValue,
                    AlertType.High or AlertType.UrgentHigh => glucoseValue >= thresholdValue,
                    _ => false,
                };
            }

            if (isConditionMet)
            {
                // Check for existing active alert to prevent spam
                var existingAlert = await _alertHistoryRepository.GetActiveAlertForRuleAndTypeAsync(
                    rule.UserId,
                    rule.Id,
                    check.Type.ToString(),
                    cancellationToken
                );

                if (existingAlert != null)
                {
                    // Check if enough time has passed for re-alerting (cooldown)
                    var timeSinceLastAlert = DateTime.UtcNow - existingAlert.TriggerTime;
                    if (timeSinceLastAlert.TotalMinutes < _options.AlertCooldownMinutes)
                    {
                        _logger.LogDebug(
                            "Alert cooldown active for {AlertType} alert for user {UserId}",
                            check.Type,
                            rule.UserId
                        );
                        continue;
                    }
                }

                // Apply hysteresis to prevent oscillating alerts
                var valueToCheck = glucoseValue;
                if (
                    check.Type == AlertType.ForecastLow
                    && predictions?.Predictions?.Default != null
                    && rule.ForecastLeadTimeMinutes.HasValue
                )
                {
                    var targetIndex = rule.ForecastLeadTimeMinutes.Value / 5;
                    if (targetIndex < predictions.Predictions.Default.Count)
                    {
                        valueToCheck = (decimal)predictions.Predictions.Default[targetIndex];
                    }
                }

                if (
                    existingAlert != null
                    && ShouldApplyHysteresis(valueToCheck, thresholdValue, check.Type)
                )
                {
                    _logger.LogDebug(
                        "Hysteresis prevents {AlertType} alert for user {UserId} (value: {Value}, threshold: {Threshold})",
                        check.Type,
                        rule.UserId,
                        valueToCheck,
                        thresholdValue
                    );
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<bool> IsUserInQuietHours(
        string userId,
        DateTime? checkTime = null,
        CancellationToken cancellationToken = default
    )
    {
        return await _notificationPreferencesRepository.IsUserInQuietHoursAsync(
            userId,
            checkTime,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public bool EvaluateTimeBasedConditions(AlertRuleEntity rule, DateTime checkTime)
    {
        try
        {
            // Check active hours
            if (!string.IsNullOrEmpty(rule.ActiveHours))
            {
                var activeHours = JsonSerializer.Deserialize<ActiveHoursConfig>(rule.ActiveHours);
                if (activeHours != null && !IsWithinActiveHours(checkTime.TimeOfDay, activeHours))
                {
                    return false;
                }
            }

            // Check days of week
            if (!string.IsNullOrEmpty(rule.DaysOfWeek))
            {
                var daysOfWeek = JsonSerializer.Deserialize<int[]>(rule.DaysOfWeek);
                if (daysOfWeek != null && !daysOfWeek.Contains((int)checkTime.DayOfWeek))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Error evaluating time-based conditions for rule {RuleId}, allowing alert",
                rule.Id
            );
            return true; // Fail safe - allow alert if we can't parse conditions
        }
    }

    private async Task<AlertEvent?> CreateAlertEvent(
        Entry glucoseReading,
        AlertRuleEntity rule,
        CancellationToken cancellationToken,
        GlucosePredictionResponse? predictions
    )
    {
        var glucoseValue = (decimal)(glucoseReading.Sgv ?? glucoseReading.Mgdl);

        // Determine alert type and threshold
        var (alertType, threshold) = DetermineAlertTypeAndThreshold(
            glucoseValue,
            rule,
            predictions
        );
        if (!threshold.HasValue)
            return null;

        // Check for duplicate active alert
        var existingAlert = await _alertHistoryRepository.GetActiveAlertForRuleAndTypeAsync(
            rule.UserId,
            rule.Id,
            alertType.ToString(),
            cancellationToken
        );

        if (existingAlert != null)
        {
            var timeSinceLastAlert = DateTime.UtcNow - existingAlert.TriggerTime;
            if (timeSinceLastAlert.TotalMinutes < _options.AlertCooldownMinutes)
            {
                return null; // Skip due to cooldown
            }
        }

        return new AlertEvent
        {
            UserId = rule.UserId,
            AlertRuleId = rule.Id,
            AlertType = alertType,
            GlucoseValue = glucoseValue,
            Threshold = threshold.Value,
            TriggerTime = glucoseReading.Date ?? DateTime.UtcNow,
            Rule = rule,
            Context = new Dictionary<string, object>
            {
                { "EntryId", glucoseReading.Id ?? string.Empty },
                { "Direction", glucoseReading.Direction ?? string.Empty },
                { "Delta", glucoseReading.Delta ?? 0 },
                { "IsForecast", alertType == AlertType.ForecastLow },
            },
        };
    }

    private (AlertType alertType, decimal? threshold) DetermineAlertTypeAndThreshold(
        decimal glucoseValue,
        AlertRuleEntity rule,
        GlucosePredictionResponse? predictions
    )
    {
        // Check urgent thresholds first (highest priority)
        if (rule.UrgentLowThreshold.HasValue && glucoseValue <= rule.UrgentLowThreshold.Value)
        {
            return (AlertType.UrgentLow, rule.UrgentLowThreshold.Value);
        }

        if (rule.UrgentHighThreshold.HasValue && glucoseValue >= rule.UrgentHighThreshold.Value)
        {
            return (AlertType.UrgentHigh, rule.UrgentHighThreshold.Value);
        }

        // Check standard thresholds
        if (rule.LowThreshold.HasValue && glucoseValue <= rule.LowThreshold.Value)
        {
            return (AlertType.Low, rule.LowThreshold.Value);
        }

        if (rule.HighThreshold.HasValue && glucoseValue >= rule.HighThreshold.Value)
        {
            return (AlertType.High, rule.HighThreshold.Value);
        }

        // Check Forecast Low
        if (
            rule.ForecastLeadTimeMinutes.HasValue
            && rule.ForecastLeadTimeMinutes > 0
            && rule.LowThreshold.HasValue
            && predictions?.Predictions?.Default != null
        )
        {
            var targetIndex = rule.ForecastLeadTimeMinutes.Value / 5;
            if (targetIndex < predictions.Predictions.Default.Count)
            {
                var predictedValue = (decimal)predictions.Predictions.Default[targetIndex];
                if (predictedValue <= rule.LowThreshold.Value)
                {
                    return (AlertType.ForecastLow, rule.LowThreshold.Value);
                }
            }
        }

        return (AlertType.Low, null); // No threshold met
    }

    private bool ShouldApplyHysteresis(decimal currentValue, decimal threshold, AlertType alertType)
    {
        var hysteresisAmount = threshold * (decimal)_options.HysteresisPercentage;

        return alertType switch
        {
            AlertType.Low or AlertType.UrgentLow or AlertType.ForecastLow => currentValue
                > (threshold + hysteresisAmount),
            AlertType.High or AlertType.UrgentHigh => currentValue < (threshold - hysteresisAmount),
            _ => false,
        };
    }

    private bool IsWithinActiveHours(TimeSpan currentTime, ActiveHoursConfig activeHours)
    {
        var start = TimeSpan
            .FromHours(activeHours.StartHour)
            .Add(TimeSpan.FromMinutes(activeHours.StartMinute));
        var end = TimeSpan
            .FromHours(activeHours.EndHour)
            .Add(TimeSpan.FromMinutes(activeHours.EndMinute));

        // Handle active hours that span midnight
        if (start <= end)
        {
            return currentTime >= start && currentTime <= end;
        }
        else
        {
            return currentTime >= start || currentTime <= end;
        }
    }

    /// <summary>
    /// Configuration for active hours
    /// </summary>
    private class ActiveHoursConfig
    {
        public int StartHour { get; set; }
        public int StartMinute { get; set; }
        public int EndHour { get; set; }
        public int EndMinute { get; set; }
    }
}
