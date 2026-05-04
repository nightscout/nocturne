namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Shared decimal comparison helper for condition evaluators that take a string
/// operator from a serialised condition payload (threshold, IOB, COB, reservoir,
/// site age, sensor age, staleness, predicted, ...).
/// </summary>
public static class ComparisonOps
{
    /// <summary>
    /// Compares <paramref name="actual"/> against <paramref name="threshold"/> using the
    /// supplied operator. Supported operators: <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>,
    /// <c>&gt;=</c>, <c>==</c>.
    /// </summary>
    /// <param name="actual">The measured value.</param>
    /// <param name="op">The comparison operator. Unknown operators yield
    /// <see langword="false"/> rather than throwing — alert evaluation is best-effort
    /// and a malformed rule must not crash the orchestrator. Misconfiguration shows up
    /// as the rule never firing, mirroring the dead-evaluator path in
    /// <see cref="CompositeEvaluator"/>.</param>
    /// <param name="threshold">The configured threshold from the rule.</param>
    public static bool Compare(decimal actual, string op, decimal threshold) => op switch
    {
        "<" => actual < threshold,
        "<=" => actual <= threshold,
        ">" => actual > threshold,
        ">=" => actual >= threshold,
        "==" => actual == threshold,
        _ => false
    };
}
