namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Evaluates the alert rules a committed write moved, against the latest canonical reading.
/// </summary>
/// <remarks>
/// Alarms follow the canonical stream, the same series the user sees on charts and followers,
/// so a losing CGM's readings never trigger or suppress an alarm. The evaluation reads the canonical
/// latest from storage rather than trusting a written batch.
/// </remarks>
public interface ICanonicalAlertEvaluator
{
    /// <summary>
    /// Builds a sensor context from the latest canonical reading and evaluates the enabled rules that
    /// read a fact in <paramref name="moved"/> or that it names (<see cref="MovedFacts.Rules"/>), or
    /// every enabled rule when a reading not yet evaluated moved the glucose fact. No-op when no usable
    /// reading is selected. Failures are logged and swallowed; the caller's own cancellation
    /// propagates.
    /// </summary>
    /// <remarks>
    /// With <see cref="ReadingReport.ByCaller"/> the selected reading is not reported to the
    /// clock-anomaly notice: the caller reports it once for the whole run it is part of.
    /// </remarks>
    /// <returns>
    /// Whether a pass of every enabled rule over the selected reading ran to its end, whatever it
    /// decided: false when the glucose fact did not move, the reading was already claimed, or the pass
    /// threw before it ended.
    /// </returns>
    Task<bool> EvaluateAsync(
        MovedFacts moved, ReadingReport report = ReadingReport.Here, CancellationToken ct = default);
}
