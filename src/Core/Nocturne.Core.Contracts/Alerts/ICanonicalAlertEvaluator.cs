namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Evaluates glucose alert rules against the latest canonical reading.
/// </summary>
/// <remarks>
/// Alarms follow the canonical stream — the same series the user sees on charts and followers —
/// so a losing CGM's readings never trigger or suppress an alarm. Callers invoke this after a
/// glucose write has been persisted; the evaluation reads the canonical latest from storage
/// rather than trusting the just-written batch.
/// </remarks>
public interface ICanonicalAlertEvaluator
{
    /// <summary>
    /// Builds a sensor context from the latest canonical reading and evaluates alert rules.
    /// No-op when no reading exists in the recent window. Failures are logged and swallowed —
    /// alert evaluation never fails a write.
    /// </summary>
    Task EvaluateAsync(CancellationToken ct = default);
}
