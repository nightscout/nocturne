using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Models;

/// <summary>
/// Snapshot of current sensor state provided to condition evaluators.
/// All glucose values are in mg/dL; rate is mg/dL per minute.
/// </summary>
public record SensorContext
{
    /// <summary>
    /// Most recent glucose value in mg/dL, or null if no reading is available.
    /// </summary>
    public required decimal? LatestValue { get; init; }

    /// <summary>
    /// Timestamp of the most recent glucose reading.
    /// </summary>
    public required DateTime? LatestTimestamp { get; init; }

    /// <summary>
    /// Rate of glucose change in mg/dL per minute. Positive = rising, negative = falling.
    /// </summary>
    public required decimal? TrendRate { get; init; }

    /// <summary>
    /// Timestamp of the last reading received from the CGM, used for signal loss detection.
    /// </summary>
    public required DateTime? LastReadingAt { get; init; }
}

// ----- Condition parameter records (deserialized from JSONB) -----

/// <summary>
/// Threshold-based alert condition. Triggers when glucose crosses <paramref name="Value"/> in the specified <paramref name="Direction"/>.
/// </summary>
/// <param name="Direction">Comparison direction: "above" or "below".</param>
/// <param name="Value">Glucose threshold in mg/dL.</param>
public record ThresholdCondition(string Direction, decimal Value);

/// <summary>
/// Rate-of-change alert condition. Triggers when glucose change rate exceeds <paramref name="Rate"/> in the specified <paramref name="Direction"/>.
/// </summary>
/// <param name="Direction">Rate direction: "rising" or "falling".</param>
/// <param name="Rate">Rate threshold in mg/dL per minute.</param>
public record RateOfChangeCondition(string Direction, decimal Rate);

/// <summary>
/// Signal loss alert condition. Triggers when no CGM reading is received for <paramref name="TimeoutMinutes"/>.
/// </summary>
/// <param name="TimeoutMinutes">Minutes without a reading before triggering.</param>
public record SignalLossCondition(int TimeoutMinutes);

/// <summary>
/// Composite alert condition combining multiple child conditions with a logical operator.
/// </summary>
/// <param name="Operator">Logical operator: "and" or "or".</param>
/// <param name="Conditions">Child condition nodes to evaluate.</param>
public record CompositeCondition(string Operator, List<ConditionNode> Conditions);

/// <summary>
/// A polymorphic condition node in the alert rule condition tree.
/// Exactly one of the optional parameters is populated based on <paramref name="Type"/>.
/// </summary>
/// <param name="Type">Condition type: "threshold", "rateOfChange", "signalLoss", or "composite".</param>
/// <param name="Threshold">Threshold condition parameters, populated when <paramref name="Type"/> is "threshold".</param>
/// <param name="RateOfChange">Rate-of-change parameters, populated when <paramref name="Type"/> is "rateOfChange".</param>
/// <param name="SignalLoss">Signal loss parameters, populated when <paramref name="Type"/> is "signalLoss".</param>
/// <param name="Composite">Composite parameters, populated when <paramref name="Type"/> is "composite".</param>
public record ConditionNode(
    string Type,
    ThresholdCondition? Threshold = null,
    RateOfChangeCondition? RateOfChange = null,
    SignalLossCondition? SignalLoss = null,
    CompositeCondition? Composite = null
);

/// <summary>
/// State machine states for excursion tracking within an <see cref="AlertTrackerState"/>.
/// </summary>
public enum TrackerState { Idle, Confirming, Active, Hysteresis }

// ----- Domain models for alert tracker persistence -----

/// <summary>
/// Per-rule state machine tracker. Maps 1:1 with an <see cref="AlertRule"/>.
/// States: idle -> confirming -> active -> hysteresis -> idle.
/// </summary>
/// <seealso cref="AlertRule"/>
/// <seealso cref="AlertExcursion"/>
public class AlertTrackerState
{
    /// <summary>
    /// The <see cref="AlertRule"/> this tracker monitors.
    /// </summary>
    public Guid AlertRuleId { get; set; }

    /// <summary>
    /// Current state machine state. One of: "idle", "confirming", "active", "hysteresis".
    /// </summary>
    /// <seealso cref="TrackerState"/>
    public string State { get; set; } = "idle";

    /// <summary>
    /// Number of consecutive readings that have confirmed the condition during the "confirming" state.
    /// </summary>
    public int ConfirmationCount { get; set; }

    /// <summary>
    /// The currently active <see cref="AlertExcursion"/> ID, set when state transitions to "active".
    /// </summary>
    public Guid? ActiveExcursionId { get; set; }

    /// <summary>
    /// Timestamp of the last state transition.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A composable alert rule with condition tree, hysteresis, and confirmation settings.
/// </summary>
/// <seealso cref="AlertTrackerState"/>
/// <seealso cref="AlertExcursion"/>
/// <seealso cref="Alerts.AlertConditionType"/>
/// <seealso cref="Alerts.AlertRuleSeverity"/>
public class AlertRule
{
    /// <summary>Unique identifier for the rule.</summary>
    public Guid Id { get; set; }

    /// <summary>Human-readable rule name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description of what this rule monitors.</summary>
    public string? Description { get; set; }

    /// <summary>The type of condition this rule evaluates.</summary>
    /// <seealso cref="Alerts.AlertConditionType"/>
    public AlertConditionType ConditionType { get; set; } = AlertConditionType.Threshold;

    /// <summary>JSON-serialized condition parameters (deserialized into <see cref="ConditionNode"/>).</summary>
    public string ConditionParams { get; set; } = "{}";

    /// <summary>Minutes to wait after condition clears before transitioning back to idle.</summary>
    public int HysteresisMinutes { get; set; }

    /// <summary>Number of consecutive readings required to confirm the condition before triggering.</summary>
    public int ConfirmationReadings { get; set; } = 1;

    /// <summary>Severity level of alerts generated by this rule.</summary>
    /// <seealso cref="Alerts.AlertRuleSeverity"/>
    public AlertRuleSeverity Severity { get; set; } = AlertRuleSeverity.Warning;

    /// <summary>JSON-serialized client configuration (sound, vibration, display preferences).</summary>
    public string ClientConfiguration { get; set; } = "{}";

    /// <summary>Whether this rule is actively being evaluated.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Display order among rules.</summary>
    public int SortOrder { get; set; }

    /// <summary>When this rule was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this rule was last modified.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A single continuous excursion (out-of-range episode) for an <see cref="AlertRule"/>.
/// </summary>
/// <seealso cref="AlertRule"/>
/// <seealso cref="AlertTrackerState"/>
public class AlertExcursion
{
    /// <summary>Unique identifier for this excursion.</summary>
    public Guid Id { get; set; }

    /// <summary>The <see cref="AlertRule"/> that triggered this excursion.</summary>
    public Guid AlertRuleId { get; set; }

    /// <summary>When the excursion began.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>When the excursion ended. Null if still active.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>When a user acknowledged this excursion.</summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>Identifier of the user who acknowledged this excursion.</summary>
    public string? AcknowledgedBy { get; set; }

    /// <summary>When the hysteresis cooldown period began after the condition cleared.</summary>
    public DateTime? HysteresisStartedAt { get; set; }
}

/// <summary>
/// Structured alert payload delivered to notification providers (push, email, etc.).
/// Contains all data needed to render an alert message; no pre-rendered text.
/// </summary>
/// <seealso cref="AlertRule"/>
/// <seealso cref="AlertExcursion"/>
public record AlertPayload
{
    /// <summary>The condition type that triggered this alert.</summary>
    public required AlertConditionType AlertType { get; init; }

    /// <summary>Human-readable name of the <see cref="AlertRule"/> that fired.</summary>
    public required string RuleName { get; init; }

    /// <summary>Current glucose value in mg/dL at the time of the alert.</summary>
    public required decimal? GlucoseValue { get; init; }

    /// <summary>Glucose trend direction string (e.g., "Flat", "SingleUp").</summary>
    public required string? Trend { get; init; }

    /// <summary>Rate of glucose change in mg/dL per minute.</summary>
    public required decimal? TrendRate { get; init; }

    /// <summary>Timestamp of the glucose reading that triggered the alert.</summary>
    public required DateTime ReadingTimestamp { get; init; }

    /// <summary>The <see cref="AlertExcursion"/> that this alert belongs to.</summary>
    public required Guid ExcursionId { get; init; }

    /// <summary>The alert instance ID for delivery tracking.</summary>
    public required Guid InstanceId { get; init; }

    /// <summary>Tenant (user) this alert belongs to.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>Display name of the subject (person being monitored).</summary>
    public required string SubjectName { get; init; }

    /// <summary>Total number of active excursions across all rules for this tenant.</summary>
    public required int ActiveExcursionCount { get; init; }
}
