using System.Data.Common;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Default <see cref="ICanonicalAlertEvaluator"/>: selects the reading an alert context is built
/// from (<see cref="AlertReadings"/>) and hands a <see cref="SensorContext"/> and the rules the moved
/// facts select to the <see cref="IAlertOrchestrator"/>.
/// </summary>
/// <remarks>
/// A reading not yet evaluated selects every enabled rule (docs/alerts/engine-semantics.md §5.2); any
/// other moved fact selects the rules that read it (<see cref="RuleTrees.Reads"/>), glucose included
/// when it moved without a reading (<see cref="MovedFacts.GlucoseWithoutReading"/>), and a rule whose
/// facts the engine cannot read is selected by every write. A saved rule (<see cref="MovedFacts.Rules"/>)
/// is selected whatever it reads, and alone when nothing else moved. With no reading selected nothing is
/// evaluated; the sweep then evaluates every wall-clock rule. A selected reading with no glucose value
/// is evaluated with <see cref="ContextForValueless"/>.
/// <para>
/// A reading dated beyond the clock tolerance is not selected, so its arrival evaluates nothing new:
/// the selected reading is the one evaluated before it, and the watermark skips it as such. The
/// arrival is reported to <see cref="IClockAnomalyNotice"/> after the evaluation, whatever the
/// watermark says, unless the caller reports it (<see cref="ReadingReport.ByCaller"/>), and a failure
/// of the notice is only logged.
/// Writers publish in chunks and re-publish readings they have already stored, so the same
/// canonical reading arrives here many times over; <see cref="AlertEvaluationWatermark"/> collapses
/// those repeats to one pass per reading per <see cref="AlertEvaluationWatermark.MaxSkipWindow"/>,
/// claimed before the pass dispatches and released if it does not complete. A repeat still evaluates
/// the rules the batch's other facts select. A pass on a reading it claimed that decided every
/// enabled rule records the reading (<see cref="IEvaluatedReadingStore"/>); the sweep evaluates a
/// newest reading with no such record (<see cref="AlertSweepJob.LostEvaluationGrace"/>). Hysteresis
/// closure, snooze expiry, auto-resolve and wall-clock rules (<see cref="RuleTrees.ReferencesWallClock"/>)
/// are also driven by the timer in <see cref="AlertSweepJob"/>.
/// </para>
/// </remarks>
internal sealed class CanonicalAlertEvaluator : ICanonicalAlertEvaluator
{
    private readonly ICanonicalGlucoseService _canonicalGlucose;
    private readonly IAlertOrchestrator _alertOrchestrator;
    private readonly IAlertRepository _alertRepository;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly AlertEvaluationWatermark _watermark;
    private readonly AlertRuleDecisions _decisions;
    private readonly IEvaluatedReadingStore _evaluatedReadings;
    private readonly IClockAnomalyNotice _clockAnomalyNotice;
    private readonly ILogger<CanonicalAlertEvaluator> _logger;

    public CanonicalAlertEvaluator(
        ICanonicalGlucoseService canonicalGlucose,
        IAlertOrchestrator alertOrchestrator,
        IAlertRepository alertRepository,
        ITenantAccessor tenantAccessor,
        AlertEvaluationWatermark watermark,
        AlertRuleDecisions decisions,
        IEvaluatedReadingStore evaluatedReadings,
        IClockAnomalyNotice clockAnomalyNotice,
        ILogger<CanonicalAlertEvaluator> logger)
    {
        _canonicalGlucose = canonicalGlucose ?? throw new ArgumentNullException(nameof(canonicalGlucose));
        _alertOrchestrator = alertOrchestrator ?? throw new ArgumentNullException(nameof(alertOrchestrator));
        _alertRepository = alertRepository ?? throw new ArgumentNullException(nameof(alertRepository));
        _tenantAccessor = tenantAccessor ?? throw new ArgumentNullException(nameof(tenantAccessor));
        _watermark = watermark ?? throw new ArgumentNullException(nameof(watermark));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
        _evaluatedReadings = evaluatedReadings ?? throw new ArgumentNullException(nameof(evaluatedReadings));
        _clockAnomalyNotice = clockAnomalyNotice ?? throw new ArgumentNullException(nameof(clockAnomalyNotice));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// The base context an evaluation pass starts from for <paramref name="latest"/>, the reading
    /// <see cref="AlertReadings"/> selected, and its <paramref name="trend"/>; the
    /// orchestrator's enricher fills in the rest. The reading's age runs from when it was stored
    /// (<see cref="SensorGlucose.CreatedAt"/>), so one that arrives late is fresh on arrival.
    /// </summary>
    /// <remarks>
    /// An older reading that arrives later does not displace it, so it does not refresh the age.
    /// A re-upload or edit of the reading updates its row and keeps its stored time.
    /// </remarks>
    internal static SensorContext ContextFor(SensorGlucose latest, ReadingTrend trend) => new()
    {
        LatestValue = latest.Mgdl.ToDecimal(),
        LatestTimestamp = latest.Timestamp,
        LatestReceivedAt = latest.CreatedAt,
        TrendRate = trend.Rate,
        TrendBucket = trend.Bucket,
        LastReadingAt = latest.Timestamp,
        LatestSource = new ReadingSource(latest.DataSource, latest.Device),
    };

    /// <summary>
    /// The base context for <paramref name="newest"/>, a reading with no glucose value
    /// (docs/alerts/engine-semantics.md §4, sensor state). Its time and arrival are the newest
    /// reading's, so the context is that reading's; <see cref="SensorContext.LastReadingAt"/> is the
    /// last reading with a value (<paramref name="back"/>), since <c>signal_loss</c> and
    /// <c>staleness</c> mean no reading with a value; and the sensor's state says why the glucose
    /// leaves cannot read it.
    /// </summary>
    internal static SensorContext ContextForValueless(SensorGlucose newest, UsableLookBack back) => new()
    {
        LatestValue = null,
        LatestTimestamp = newest.Timestamp,
        LatestReceivedAt = newest.CreatedAt,
        TrendRate = null,
        LastReadingAt = back.At,
        SensorState = new SensorStateSnapshot(SensorState.NoValue, back.ValuelessSince),
        LatestSource = new ReadingSource(newest.DataSource, newest.Device),
    };

    /// <inheritdoc />
    public async Task<bool> EvaluateAsync(
        MovedFacts moved, ReadingReport report = ReadingReport.Here, CancellationToken ct = default)
    {
        var ran = false;
        try
        {
            var (readAt, now) = _decisions.BeginRead();
            var selection = await AlertReadings.SelectAsync(_canonicalGlucose, now, ct);
            ran = await EvaluateSelectedAsync(selection.Selected, readAt, moved, ct);
            if (report == ReadingReport.Here)
                await _clockAnomalyNotice.ReportReadingIsolatedAsync(selection, now, _logger, ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Alert evaluation against the canonical stream failed");
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Alert evaluation against the canonical stream failed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The write is already stored, and the evaluation runs off its request: a fault here
            // must not stop the next one.
            _logger.LogError(ex, "Alert evaluation against the canonical stream failed unexpectedly");
        }

        return ran;
    }

    /// <returns>Whether this call claimed <paramref name="latest"/> and its pass ran to its end.</returns>
    private async Task<bool> EvaluateSelectedAsync(SensorGlucose? latest, long readAt, MovedFacts moved, CancellationToken ct)
    {
        if (latest is null || moved.IsEmpty) return false;

        var tenantId = _tenantAccessor.TenantId;
        if (tenantId == Guid.Empty) return false;

        var glucose = moved.Facts.Contains(ContextFactWrites.Glucose);
        var reading = glucose && _watermark.TryClaim(tenantId, latest);
        if (glucose && !reading)
        {
            _logger.LogTrace(
                "Glucose rules skipped for tenant {TenantId}: reading at {ReadingTimestamp} was already evaluated",
                tenantId, latest.Timestamp);
        }

        var completed = false;
        AlertPassOutcome? outcome = null;
        try
        {
            var selecting = moved.Facts
                .Where(fact => fact != ContextFactWrites.Glucose || moved.GlucoseWithoutReading)
                .ToHashSet(StringComparer.Ordinal);
            if (reading || selecting.Count > 0 || moved.Rules.Count > 0)
            {
                var enabled = await _alertRepository.GetEnabledRulesAsync(ct);
                var selected = reading
                    ? enabled
                    : enabled.Where(rule => moved.Rules.Contains(rule.Id) || (selecting.Count > 0 && ReadsAny(rule, selecting))).ToList();
                if (selected.Count > 0)
                {
                    var context = AlertReadings.HasValue(latest)
                        ? ContextFor(latest, await ReadingTrend.ForAsync(latest, _canonicalGlucose, ct))
                        : ContextForValueless(latest, await AlertReadings.LookBackAsync(_canonicalGlucose, latest, ct));
                    outcome = await _alertOrchestrator.EvaluateRulesAsync(
                        selected, enabled.Select(r => r.Id).ToHashSet(), context, readAt, ct);
                }
            }
            // The claim stands even when the orchestrator skipped rules: it skips only a rule a
            // newer context has decided, which a repeat of this reading would skip too. A rule that
            // threw is undecided, so its reading is released for the next write or the sweep.
            completed = outcome != AlertPassOutcome.Failed;
        }
        finally
        {
            if (reading && !completed)
                _watermark.Release(tenantId, latest);
        }

        if (reading && outcome == AlertPassOutcome.Decided)
            await RecordEvaluatedAsync(latest, ct);
        return reading;
    }

    /// <summary>
    /// Records <paramref name="latest"/> as evaluated. A failure only costs the sweep one more
    /// evaluation of the reading, so it is logged and the evaluation stands.
    /// </summary>
    private async Task RecordEvaluatedAsync(SensorGlucose latest, CancellationToken ct)
    {
        try
        {
            await _evaluatedReadings.RecordAsync(EvaluatedReading.Of(latest), ct);
        }
        catch (DbException ex)
        {
            _logger.LogWarning(ex, "Could not record the reading at {ReadingTimestamp} as evaluated", latest.Timestamp);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Could not record the reading at {ReadingTimestamp} as evaluated", latest.Timestamp);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Could not record the reading at {ReadingTimestamp} as evaluated", latest.Timestamp);
        }
    }

    private bool ReadsAny(AlertRuleSnapshot rule, IReadOnlySet<string> facts)
    {
        try
        {
            return RuleTrees.Reads(rule).Any(facts.Contains);
        }
        catch (RustAlertEngineException ex)
        {
            _logger.LogWarning(ex,
                "The engine could not read the facts rule {AlertRuleId} reads; every write evaluates it", rule.Id);
            return true;
        }
    }
}
