using System.Collections.Concurrent;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Remembers, per tenant, the canonical reading the last completed alert evaluation ran against,
/// so a write that did not move the canonical stream does not repeat the pass.
/// </summary>
/// <remarks>
/// A single sync publishes glucose in chunks — a page per fetch, a batch per page — and an
/// uploader that re-posts already-stored readings publishes on every request; each of those calls
/// the evaluator, and each evaluation re-decides against the same latest canonical reading.
/// Singleton because <see cref="ICanonicalAlertEvaluator"/> is scoped and those chunks span
/// scopes. Keyed by tenant, so the entry count is bounded by tenant count.
/// <para>
/// The skip expires after <see cref="MaxSkipWindow"/>. A rule can turn true on the clock alone
/// while the reading stands still, e.g. a <c>time_of_day</c> gate opening over a low that the
/// wall-clock sweep does not evaluate (<see cref="Engines.RuleTrees.ReferencesWallClock"/>). So the reading is a
/// reason to skip a repeat, never a reason to stop evaluating. The window also bounds a rule the user has just created or edited.
/// </para>
/// </remarks>
/// <seealso cref="CanonicalAlertEvaluator"/>
internal sealed class AlertEvaluationWatermark(TimeProvider timeProvider)
{
    /// <summary>
    /// Longest a repeat pass may be skipped. Comparable to the interval between the repeats it
    /// replaces (the measured ~8 publishes per reading), so a clock-driven rule is at most this
    /// late rather than indefinitely frozen.
    /// </summary>
    internal static readonly TimeSpan MaxSkipWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Measured with <see cref="TimeProvider.GetTimestamp"/> rather than a wall clock: an NTP
    /// correction that steps the clock backwards would make the age of a recorded pass negative,
    /// which reads as "well inside the window" and reinstates the unbounded skip this window
    /// exists to prevent, for as long as the step.
    /// </summary>
    private readonly TimeProvider _timeProvider = timeProvider;

    private readonly ConcurrentDictionary<Guid, Pass> _lastPass = new();

    /// <summary>
    /// Claims the pass over <paramref name="reading"/> for the tenant, unless one inside the last
    /// <see cref="MaxSkipWindow"/> already claimed it. Claimed before the pass dispatches, so a pass
    /// that overlaps it on the same reading stops after its canonical read rather than waiting on, or
    /// repeating, the first pass's delivery.
    /// </summary>
    /// <returns>Whether the caller should evaluate the reading.</returns>
    /// <remarks>
    /// A claim is keyed by exactly the reading (<see cref="EvaluatedReading"/>). A
    /// <see cref="ReadingTrend"/> derived from an earlier reading is not in the key: deriving it costs
    /// the read a skipped repeat exists to save, and an earlier reading that lands late is judged once
    /// <see cref="MaxSkipWindow"/> lapses or the next reading arrives.
    /// <para>
    /// <see cref="Guid.Empty"/> means no tenant was in scope; nothing is remembered under it, so
    /// every claim on it is granted.
    /// </para>
    /// </remarks>
    public bool TryClaim(Guid tenantId, SensorGlucose reading)
    {
        if (tenantId == Guid.Empty) return true;

        var claim = new Pass(EvaluatedReading.Of(reading), _timeProvider.GetTimestamp());
        while (true)
        {
            if (!_lastPass.TryGetValue(tenantId, out var previous))
            {
                if (_lastPass.TryAdd(tenantId, claim)) return true;
                continue;
            }

            if (previous.Reading == claim.Reading && _timeProvider.GetElapsedTime(previous.At) < MaxSkipWindow)
                return false;
            if (_lastPass.TryUpdate(tenantId, claim, previous)) return true;
        }
    }

    /// <summary>
    /// Drops the claim on <paramref name="reading"/> when its pass did not complete, so the next
    /// write, or the sweep, evaluates it again. A newer claim is left alone.
    /// </summary>
    public void Release(Guid tenantId, SensorGlucose reading)
    {
        if (_lastPass.TryGetValue(tenantId, out var claimed) && claimed.Reading == EvaluatedReading.Of(reading))
            _lastPass.TryRemove(new KeyValuePair<Guid, Pass>(tenantId, claimed));
    }

    private readonly record struct Pass(EvaluatedReading Reading, long At);
}
