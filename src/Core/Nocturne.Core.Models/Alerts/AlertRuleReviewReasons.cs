namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Values of an alert rule's review reason: why a data repair changed or disabled the rule, so
/// its owner is shown it until the next edit.
/// </summary>
/// <remarks>
/// Stored as plain strings rather than an enum column: an enum converter throws on a value it does
/// not know, and one unreadable rule stops every rule of its tenant being read.
/// </remarks>
public static class AlertRuleReviewReasons
{
    /// <summary>
    /// A rule stored before the alerts redesign, in a shape the engine reads wrongly, rewritten to
    /// the shape it was meant to have. Its confirmation readings and hysteresis were dropped with
    /// their columns, so it fires on the first reading that meets its condition.
    /// </summary>
    public const string LegacyConditionConverted = "legacy_condition_converted";

    /// <summary>
    /// <see cref="LegacyConditionConverted"/> for a rule with a glucose threshold that is missing,
    /// not a number, or below 40 mg/dL once converted (likely a mmol/L value typed into an mg/dL
    /// field).
    /// </summary>
    public const string LegacyThresholdImplausible = "legacy_threshold_implausible";

    /// <summary>
    /// A rule whose condition type no evaluator knows. It was disabled; its condition is kept.
    /// </summary>
    public const string ConditionTypeUnrecognized = "condition_type_unrecognized";

    /// <summary>A rule whose severity was not a known one. It was made critical.</summary>
    public const string SeverityUnrecognized = "severity_unrecognized";
}
