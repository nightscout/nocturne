using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Shared comparison helper for time-since-last-event leaves
/// (<see cref="TimeSinceLastCarbEvaluator"/>, <see cref="TimeSinceLastBolusEvaluator"/>).
/// Encapsulates the cold-start convention: when no anchor timestamp exists, elapsed minutes
/// is <c>+∞</c>, so predicates of the form "<c>&gt;= N min</c>" fire and "<c>&lt; N min</c>"
/// fail. Unknown operators fail closed (return false), matching the silent fail-mode used
/// throughout the alert engine.
/// </summary>
public static class TimeSinceComparator
{
    /// <summary>
    /// Returns whether (now - <paramref name="anchor"/>).TotalMinutes <paramref name="op"/>
    /// <paramref name="threshold"/>. When <paramref name="anchor"/> is null the elapsed value
    /// is treated as <see cref="double.PositiveInfinity"/>.
    /// </summary>
    public static bool Apply(DateTime now, DateTime? anchor, AlertComparisonOperator op, int threshold)
    {
        var elapsed = anchor is null ? double.PositiveInfinity : (now - anchor.Value).TotalMinutes;
        return op switch
        {
            AlertComparisonOperator.Gt => elapsed > threshold,
            AlertComparisonOperator.Gte => elapsed >= threshold,
            AlertComparisonOperator.Lt => elapsed < threshold,
            AlertComparisonOperator.Lte => elapsed <= threshold,
            AlertComparisonOperator.Eq => elapsed == threshold,
            _ => false,
        };
    }
}
