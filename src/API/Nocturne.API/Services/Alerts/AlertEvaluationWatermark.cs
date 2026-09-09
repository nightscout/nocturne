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
/// The reading is recorded only after a pass completes, so an evaluation that threw is retried by
/// the next write instead of being skipped as already done.
/// </para>
/// </remarks>
/// <seealso cref="CanonicalAlertEvaluator"/>
internal sealed class AlertEvaluationWatermark
{
    private readonly ConcurrentDictionary<Guid, EvaluatedReading> _lastEvaluated = new();

    /// <summary>
    /// True when the last completed pass for this tenant already evaluated exactly this reading.
    /// Compares the three fields that make up the evaluated <c>SensorContext</c> rather than the
    /// row's identity: a corrected value or trend at an unchanged timestamp is a different
    /// decision and must be evaluated, and a same-valued replacement row is not.
    /// </summary>
    public bool AlreadyEvaluated(Guid tenantId, SensorGlucose reading) =>
        _lastEvaluated.TryGetValue(tenantId, out var previous) && previous == Snapshot(reading);

    /// <summary>
    /// Records the reading a completed pass evaluated, replacing any earlier one for the tenant.
    /// </summary>
    public void Record(Guid tenantId, SensorGlucose reading) =>
        _lastEvaluated[tenantId] = Snapshot(reading);

    private static EvaluatedReading Snapshot(SensorGlucose reading) =>
        new(reading.Timestamp, reading.Mgdl, reading.TrendRate);

    private readonly record struct EvaluatedReading(DateTime Timestamp, double Mgdl, double? TrendRate);
}
