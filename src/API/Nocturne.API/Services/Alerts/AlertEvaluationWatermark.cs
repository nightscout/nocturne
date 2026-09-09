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
/// The skip expires after <see cref="MaxSkipWindow"/>. A rule can turn true on elapsed time alone
/// while the reading stands still — <c>alert_state</c> with <c>forMinutes</c>, the documented way
/// to express delayed escalation, is the sharpest case, and nothing in <see cref="AlertSweepService"/>
/// opens an excursion for it — so the reading is a reason to skip a repeat, never a reason to stop
/// evaluating. The window also bounds a rule the user has just created or edited, and a pass the
/// orchestrator completed with some rule's exception swallowed internally.
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

    private readonly ConcurrentDictionary<Guid, Pass> _lastPass = new();

    /// <summary>
    /// True when a pass inside the last <see cref="MaxSkipWindow"/> already evaluated exactly this
    /// reading. Compares the three fields that make up the evaluated <c>SensorContext</c> rather
    /// than the row's identity: a corrected value or trend at an unchanged timestamp is a
    /// different decision and must be evaluated, and a same-valued replacement row is not.
    /// </summary>
    public bool AlreadyEvaluated(Guid tenantId, SensorGlucose reading) =>
        _lastPass.TryGetValue(tenantId, out var previous)
        && previous.Reading == Key(reading)
        && timeProvider.GetUtcNow() - previous.At < MaxSkipWindow;

    /// <summary>
    /// Records the reading a completed pass evaluated, replacing any earlier one for the tenant.
    /// </summary>
    /// <remarks>
    /// <see cref="Guid.Empty"/> means no tenant was in scope, and the orchestrator returns before
    /// its first read in that case; nothing was evaluated, so nothing is remembered. Refusing the
    /// write here is what keeps <see cref="AlreadyEvaluated"/> from ever matching on that key.
    /// </remarks>
    public void Record(Guid tenantId, SensorGlucose reading)
    {
        if (tenantId == Guid.Empty) return;
        _lastPass[tenantId] = new Pass(Key(reading), timeProvider.GetUtcNow());
    }

    private static ReadingKey Key(SensorGlucose reading) =>
        new(reading.Timestamp, reading.Mgdl, reading.TrendRate);

    private readonly record struct ReadingKey(DateTime Timestamp, double Mgdl, double? TrendRate);

    private readonly record struct Pass(ReadingKey Reading, DateTimeOffset At);
}
