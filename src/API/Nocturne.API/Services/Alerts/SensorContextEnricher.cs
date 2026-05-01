using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Lazy populator for <see cref="SensorContext"/> optional fields. Walks the rules being
/// evaluated this pass once, decides which optional fields need real values, and only fetches
/// data the rules will actually consult.
/// </summary>
/// <remarks>
/// Why lazy: a tenant whose only enabled rule is a glucose threshold should not pay for an
/// IOB calculation, a treatments query, a predictions call, and a device-events lookup on
/// every reading. Each branch in <see cref="EnrichAsOfAsync"/> is gated on the corresponding
/// flag in <see cref="DataNeedsSet"/>.
///
/// Treatments are fetched once and shared between IOB and COB when both are needed — they
/// are the most expensive shared dependency.
///
/// <see cref="IPredictionService"/> is resolved through the service provider so the alert
/// engine continues to function in deployments where prediction is not configured (the type
/// is registered conditionally based on <c>PredictionOptions</c>).
///
/// Live and replay paths share a single internal dispatch; <see cref="EnrichAsync"/>
/// passes "now" as the as-of timestamp, and <c>EnrichAsOfAsync</c> passes the replay tick.
/// Replay skips the prediction service (intrinsically forward-looking) and the active-alerts
/// repo fetch (the replay walker maintains its own running set across the window and threads
/// it in via <see cref="SensorContext.ActiveAlerts"/>).
///
/// Data-source dependencies are bundled into <see cref="SensorContextEnricherDependencies"/>
/// so this constructor stays focused on orchestration concerns (service provider for the
/// optional prediction service, time, logging) rather than plumbing.
/// </remarks>
internal sealed class SensorContextEnricher : ISensorContextEnricher
{
    /// <summary>How far back to fetch treatments for IOB/COB. Bolus/temp-basal effects
    /// decay well within this window for any practical insulin, and the legacy COB algorithm
    /// only ever decays carbs over a similar horizon.</summary>
    private const int TreatmentLookbackHours = 24;

    /// <summary>Hard cap on treatments returned by the underlying paged service.</summary>
    private const int TreatmentFetchLimit = 500;

    /// <summary>Predictions are produced at fixed intervals from "now"; this is the cadence
    /// used by both AID device-status uploads and the oref WASM curve.</summary>
    private const int PredictionIntervalMinutes = 5;

    private readonly SensorContextEnricherDependencies _deps;
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SensorContextEnricher> _logger;

    public SensorContextEnricher(
        SensorContextEnricherDependencies deps,
        IServiceProvider serviceProvider,
        TimeProvider timeProvider,
        ILogger<SensorContextEnricher> logger)
    {
        _deps = deps;
        _serviceProvider = serviceProvider;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<SensorContext> EnrichAsync(
        SensorContext baseContext,
        IEnumerable<AlertRuleSnapshot> rules,
        Guid tenantId,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return EnrichInternalAsync(baseContext, rules, tenantId, now, isReplay: false, ct);
    }

    /// <inheritdoc/>
    public Task<SensorContext> EnrichAsOfAsync(
        SensorContext baseContext,
        IEnumerable<AlertRuleSnapshot> rules,
        Guid tenantId,
        DateTime asOf,
        CancellationToken ct)
        => EnrichInternalAsync(baseContext, rules, tenantId, asOf, isReplay: true, ct);

    /// <summary>
    /// Single dispatch path used by both <see cref="EnrichAsync"/> and <see cref="EnrichAsOfAsync"/>.
    /// <paramref name="now"/> is the time every fetch is pinned to (the live clock for the
    /// orchestrator path, the replay tick for replay). <paramref name="isReplay"/> toggles
    /// branches that don't have a sensible historical answer (predictions, active alerts).
    /// </summary>
    private async Task<SensorContext> EnrichInternalAsync(
        SensorContext baseContext,
        IEnumerable<AlertRuleSnapshot> rules,
        Guid tenantId,
        DateTime now,
        bool isReplay,
        CancellationToken ct)
    {
        var needs = RuleDataNeeds.Walk(rules);
        var enriched = baseContext;

        if (needs.NeedsIob || needs.NeedsCob)
        {
            var treatments = await FetchRecentTreatmentsAsync(now, ct);

            if (needs.NeedsIob)
            {
                var iobUnits = await ComputeIobAsync(treatments, ct);
                enriched = enriched with { IobUnits = iobUnits };
            }

            if (needs.NeedsCob)
            {
                var cobGrams = await ComputeCobAsync(treatments, ct);
                enriched = enriched with { CobGrams = cobGrams };
            }
        }

        if (needs.NeedsPredicted)
        {
            // Predictions are forward-from-now from the prediction service's perspective —
            // there is no documented "as of past tick" mode. During replay we surface an empty
            // list (matching the no-service / no-readings paths) so a `predicted` rule fails
            // closed rather than silently leaking the live curve into a historical window.
            var predictions = isReplay
                ? Array.Empty<PredictedGlucosePoint>()
                : await FetchPredictionsAsync(ct);
            enriched = enriched with { Predictions = predictions };
        }

        if (needs.NeedsReservoir)
        {
            var reservoirUnits = await FetchReservoirAsync(isReplay ? now : null, ct);
            enriched = enriched with { ReservoirUnits = reservoirUnits };
        }

        if (needs.NeedsSiteAge)
        {
            var lastSiteChange = await FetchLatestEventAsync(DeviceEventType.SiteChange, isReplay ? now : null, ct);
            enriched = enriched with { LastSiteChangeAt = lastSiteChange };
        }

        if (needs.NeedsSensorAge)
        {
            var lastSensorStart = await FetchLatestEventAsync(DeviceEventType.SensorStart, isReplay ? now : null, ct);
            enriched = enriched with { LastSensorStartAt = lastSensorStart };
        }

        if (needs.NeedsTrendBucket)
        {
            enriched = enriched with { TrendBucket = DeriveTrendBucket(baseContext.TrendRate) };
        }

        if (needs.NeedsActiveAlerts)
        {
            // Live: pull the canonical snapshot from the alert repo. Replay: the walker
            // computes its own running tally across the replay window and threads it into
            // baseContext.ActiveAlerts before each tick — preserve that as-is.
            if (!isReplay)
            {
                var activeAlerts = await _deps.Alerts.GetActiveAlertSnapshotsAsync(tenantId, ct);
                enriched = enriched with { ActiveAlerts = activeAlerts };
            }
        }

        enriched = await EnrichLoopingFactsAsync(enriched, needs, now, isReplay, ct);

        return enriched;
    }

    /// <summary>
    /// Populates the looping-related facts (APS cycle/enaction/sensitivity, pump status and
    /// suspension, temp basal, uploader, override) when their corresponding needs flags are
    /// set. Extracted from <see cref="EnrichInternalAsync"/> to keep that method focused on
    /// dispatch — this section grew to ~90 LOC of fetch-and-project once cold-start flags
    /// and freshness gating landed.
    /// </summary>
    private async Task<SensorContext> EnrichLoopingFactsAsync(
        SensorContext baseCtx, DataNeedsSet needs, DateTime now, bool isReplay, CancellationToken ct)
    {
        var enriched = baseCtx;

        // For live calls (isReplay=false) the asOf parameter is null — repos return the
        // absolute latest. For replay we pin to `now` (the replay tick). The HasEver* flag
        // is computed from the same as-of-bounded result, so a fact only "exists" if it was
        // observed by `now`; future facts must not leak backwards into the replay window.
        DateTime? asOf = isReplay ? now : null;

        if (needs.NeedsLastApsCycle || needs.NeedsLastApsEnacted || needs.NeedsSensitivityRatio)
        {
            if (needs.NeedsLastApsCycle)
            {
                var t = await _deps.ApsSnapshots.GetLatestTimestampAsync(asOf, ct);
                enriched = enriched with { LastApsCycleAt = t, HasEverApsCycled = t.HasValue };
            }
            if (needs.NeedsLastApsEnacted)
            {
                var t = await _deps.ApsSnapshots.GetLatestEnactedTimestampAsync(asOf, ct);
                enriched = enriched with { LastApsEnactedAt = t };
            }
            if (needs.NeedsSensitivityRatio)
            {
                var s = await _deps.ApsSnapshots.GetLatestSensitivityRatioAsync(asOf, ct);
                enriched = enriched with { SensitivityRatio = s, HasEverApsSensitivity = s.HasValue };
            }
        }

        if (needs.NeedsPumpStatus)
        {
            var pump = await _deps.PumpSnapshots.GetLatestAsync(asOf, ct);
            if (pump is not null)
            {
                enriched = enriched with
                {
                    PumpBatteryPercent = pump.BatteryPercent.HasValue ? (decimal?)pump.BatteryPercent.Value : null,
                    HasEverPumpSnapshot = true,
                };

                // Freshness gate: only project active pump-suspension when the underlying pump
                // snapshot is itself fresh — prevents suspension state latching after the
                // uploader goes offline.
                // Strictly less-than: a snapshot exactly at the threshold is treated as stale,
                // biasing toward "unknown" rather than "still suspended" at the edge of upload timing.
                var pumpFresh = (now - pump.Timestamp) < _deps.Options.Value.PumpFreshnessThreshold;
                if (pumpFresh)
                {
                    var span = await _deps.StateSpans.GetActiveAtAsync(
                        StateSpanCategory.PumpMode,
                        state: PumpModeState.Suspended.ToString(),
                        at: now,
                        ct);
                    if (span is not null)
                    {
                        enriched = enriched with
                        {
                            ActivePumpSuspension = new PumpSuspensionSnapshot(span.StartTimestamp)
                        };
                    }
                }
            }
        }

        if (needs.NeedsTempBasal)
        {
            var temp = await _deps.TempBasals.GetActiveAtAsync(now, ct);
            if (temp is not null)
            {
                enriched = enriched with { ActiveTempBasal = ProjectTempBasal(temp) };
            }
        }

        if (needs.NeedsUploaderStatus)
        {
            var uploader = await _deps.UploaderSnapshots.GetLatestAsync(asOf, ct);
            if (uploader is not null)
            {
                enriched = enriched with
                {
                    UploaderBatteryPercent = uploader.Battery.HasValue ? (decimal?)uploader.Battery.Value : null,
                    HasEverUploaderSnapshot = true,
                };
            }
        }

        if (needs.NeedsOverride)
        {
            var span = await _deps.StateSpans.GetActiveAtAsync(
                StateSpanCategory.Override, state: null, at: now, ct);
            if (span is not null)
            {
                var multiplier = span.Metadata.TryReadDecimal("insulinNeedsScaleFactor");
                var name = span.Metadata.TryReadString("reasonDisplay");
                enriched = enriched with
                {
                    ActiveOverride = new OverrideSnapshot(
                        span.StartTimestamp, span.EndTimestamp, multiplier, name)
                };
            }
        }

        return enriched;
    }

    private static TempBasalSnapshot ProjectTempBasal(TempBasal t) =>
        new(
            Rate: (decimal)t.Rate,
            ScheduledRate: t.ScheduledRate.HasValue ? (decimal?)t.ScheduledRate.Value : null,
            StartedAt: t.StartTimestamp);

    /// <summary>
    /// Fetches treatments within the IOB/COB lookback window ending at <paramref name="now"/>.
    /// The treatment service does not currently support an arbitrary as-of cutoff in its
    /// pagination API, so we fetch a bounded page (newest-first) and slice client-side.
    /// For the live path that's identical to the previous behaviour; for replay the upper
    /// bound is the replay tick rather than wall-clock now.
    /// </summary>
    private async Task<List<Treatment>> FetchRecentTreatmentsAsync(DateTime now, CancellationToken ct)
    {
        var nowMills = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var cutoffMills = nowMills - (TreatmentLookbackHours * 60L * 60L * 1000L);

        var page = await _deps.Treatments.GetTreatmentsAsync(TreatmentFetchLimit, 0, ct);
        return page.Where(t => t.Mills >= cutoffMills && t.Mills <= nowMills).ToList();
    }

    private async Task<decimal?> ComputeIobAsync(List<Treatment> treatments, CancellationToken ct)
    {
        try
        {
            var result = await _deps.Iob.CalculateTotalAsync(treatments, ct: ct);
            return (decimal)result.Iob;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute IOB for alert evaluation; leaving null");
            return null;
        }
    }

    private async Task<decimal?> ComputeCobAsync(List<Treatment> treatments, CancellationToken ct)
    {
        try
        {
            var result = await _deps.Cob.CobTotalAsync(treatments, ct: ct);
            return (decimal)result.Cob;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute COB for alert evaluation; leaving null");
            return null;
        }
    }

    private async Task<IReadOnlyList<PredictedGlucosePoint>> FetchPredictionsAsync(CancellationToken ct)
    {
        // Optional dependency — registered conditionally based on PredictionOptions.
        // GetService<> returns null when unavailable (PredictionSource.None or DI not wired).
        var predictionService = _serviceProvider.GetService<IPredictionService>();
        if (predictionService is null)
            return Array.Empty<PredictedGlucosePoint>();

        GlucosePredictionResponse response;
        try
        {
            response = await predictionService.GetPredictionsAsync(cancellationToken: ct);
        }
        catch (InvalidOperationException ex)
        {
            // Documented "no readings available" path — silent empty mirrors the leaf evaluators.
            _logger.LogWarning(ex, "Prediction service had insufficient data; returning empty predictions");
            return Array.Empty<PredictedGlucosePoint>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prediction service failed; returning empty predictions");
            return Array.Empty<PredictedGlucosePoint>();
        }

        var curve = response.Predictions.Default;
        if (curve is null || curve.Count == 0)
            return Array.Empty<PredictedGlucosePoint>();

        var interval = response.IntervalMinutes > 0 ? response.IntervalMinutes : PredictionIntervalMinutes;
        var points = new List<PredictedGlucosePoint>(curve.Count);
        for (var i = 0; i < curve.Count; i++)
        {
            // Curve index 0 is "now"; first forward step is index 1. Offset minutes are measured
            // from the current reading; the evaluator filters by WithinMinutes.
            var offsetMinutes = (i + 1) * interval;
            points.Add(new PredictedGlucosePoint(offsetMinutes, (decimal)curve[i]));
        }
        return points;
    }

    private async Task<decimal?> FetchReservoirAsync(DateTime? asOf, CancellationToken ct)
    {
        // Pin the upper bound to `asOf` for replay; live path leaves both bounds null and gets
        // the absolute latest. `to: asOf` upper-bounds the read to the replay tick (inclusive).
        var snapshots = await _deps.PumpSnapshots.GetAsync(
            from: null, to: asOf, device: null, source: null,
            limit: 1, offset: 0, descending: true, ct: ct);

        var reservoir = snapshots.FirstOrDefault()?.Reservoir;
        return reservoir is null ? null : (decimal)reservoir.Value;
    }

    private async Task<DateTime?> FetchLatestEventAsync(DeviceEventType eventType, DateTime? asOf, CancellationToken ct)
    {
        var evt = await _deps.DeviceEvents.GetLatestByEventTypeAsync(eventType, asOf, ct);
        return evt?.Timestamp;
    }

    /// <summary>
    /// Maps a glucose rate of change (mg/dL per minute) to a coarse trend bucket. Boundary
    /// values (e.g. exactly 1.0 mg/dL/min) fall into the more aggressive bucket — matches the
    /// "rising" labelling that CGM clients show at +1 mg/dL/min.
    /// </summary>
    private static TrendBucket? DeriveTrendBucket(decimal? trendRate)
    {
        if (trendRate is null)
            return null;

        var rate = trendRate.Value;
        if (rate >= 3.0m) return TrendBucket.RisingFast;
        if (rate >= 1.0m) return TrendBucket.Rising;
        if (rate >= -1.0m) return TrendBucket.Flat;
        if (rate >= -3.0m) return TrendBucket.Falling;
        return TrendBucket.FallingFast;
    }
}
