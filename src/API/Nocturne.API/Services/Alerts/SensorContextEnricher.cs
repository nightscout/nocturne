using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
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
/// every reading. Each branch in <see cref="EnrichAsync"/> is gated on the corresponding
/// flag in <see cref="DataNeedsSet"/>.
///
/// Treatments are fetched once and shared between IOB and COB when both are needed — they
/// are the most expensive shared dependency.
///
/// <see cref="IPredictionService"/> is resolved through the service provider so the alert
/// engine continues to function in deployments where prediction is not configured (the type
/// is registered conditionally based on <c>PredictionOptions</c>).
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

    /// <summary>How fresh the latest <see cref="PumpSnapshot"/> must be before we project an
    /// active pump-suspension StateSpan into the <see cref="SensorContext"/>. Twice the typical
    /// AID upload cadence — when uploads stop, suspension state is unknown, not "still suspended".</summary>
    private static readonly TimeSpan PumpFreshnessThreshold = TimeSpan.FromMinutes(10);

    private readonly IServiceProvider _serviceProvider;
    private readonly IIobService _iobService;
    private readonly ICobService _cobService;
    private readonly ITreatmentService _treatmentService;
    private readonly IDeviceEventRepository _deviceEventRepository;
    private readonly IPumpSnapshotRepository _pumpSnapshotRepository;
    private readonly IApsSnapshotRepository _apsSnapshotRepository;
    private readonly ITempBasalRepository _tempBasalRepository;
    private readonly IUploaderSnapshotRepository _uploaderSnapshotRepository;
    private readonly IStateSpanService _stateSpanService;
    private readonly IAlertRepository _alertRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SensorContextEnricher> _logger;

    public SensorContextEnricher(
        IServiceProvider serviceProvider,
        IIobService iobService,
        ICobService cobService,
        ITreatmentService treatmentService,
        IDeviceEventRepository deviceEventRepository,
        IPumpSnapshotRepository pumpSnapshotRepository,
        IApsSnapshotRepository apsSnapshotRepository,
        ITempBasalRepository tempBasalRepository,
        IUploaderSnapshotRepository uploaderSnapshotRepository,
        IStateSpanService stateSpanService,
        IAlertRepository alertRepository,
        TimeProvider timeProvider,
        ILogger<SensorContextEnricher> logger)
    {
        _serviceProvider = serviceProvider;
        _iobService = iobService;
        _cobService = cobService;
        _treatmentService = treatmentService;
        _deviceEventRepository = deviceEventRepository;
        _pumpSnapshotRepository = pumpSnapshotRepository;
        _apsSnapshotRepository = apsSnapshotRepository;
        _tempBasalRepository = tempBasalRepository;
        _uploaderSnapshotRepository = uploaderSnapshotRepository;
        _stateSpanService = stateSpanService;
        _alertRepository = alertRepository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<SensorContext> EnrichAsync(
        SensorContext baseContext,
        IEnumerable<AlertRuleSnapshot> rules,
        Guid tenantId,
        CancellationToken ct)
    {
        var needs = RuleDataNeeds.Walk(rules);
        var enriched = baseContext;

        if (needs.NeedsIob || needs.NeedsCob)
        {
            var treatments = await FetchRecentTreatmentsAsync(ct);

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
            var predictions = await FetchPredictionsAsync(ct);
            enriched = enriched with { Predictions = predictions };
        }

        if (needs.NeedsReservoir)
        {
            var reservoirUnits = await FetchReservoirAsync(ct);
            enriched = enriched with { ReservoirUnits = reservoirUnits };
        }

        if (needs.NeedsSiteAge)
        {
            var lastSiteChange = await FetchLatestEventAsync(DeviceEventType.SiteChange, ct);
            enriched = enriched with { LastSiteChangeAt = lastSiteChange };
        }

        if (needs.NeedsSensorAge)
        {
            var lastSensorStart = await FetchLatestEventAsync(DeviceEventType.SensorStart, ct);
            enriched = enriched with { LastSensorStartAt = lastSensorStart };
        }

        if (needs.NeedsTrendBucket)
        {
            enriched = enriched with { TrendBucket = DeriveTrendBucket(baseContext.TrendRate) };
        }

        if (needs.NeedsActiveAlerts)
        {
            var activeAlerts = await _alertRepository.GetActiveAlertSnapshotsAsync(tenantId, ct);
            enriched = enriched with { ActiveAlerts = activeAlerts };
        }

        // ----- Looping facts -----

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (needs.NeedsLastApsCycle || needs.NeedsLastApsEnacted || needs.NeedsSensitivityRatio)
        {
            if (needs.NeedsLastApsCycle)
            {
                var t = await _apsSnapshotRepository.GetLatestTimestampAsync(asOf: null, ct);
                enriched = enriched with { LastApsCycleAt = t, HasEverApsCycled = t.HasValue };
            }
            if (needs.NeedsLastApsEnacted)
            {
                var t = await _apsSnapshotRepository.GetLatestEnactedTimestampAsync(asOf: null, ct);
                enriched = enriched with { LastApsEnactedAt = t };
            }
            if (needs.NeedsSensitivityRatio)
            {
                var s = await _apsSnapshotRepository.GetLatestSensitivityRatioAsync(asOf: null, ct);
                enriched = enriched with { SensitivityRatio = s, HasEverApsSensitivity = s.HasValue };
            }
        }

        if (needs.NeedsPumpStatus)
        {
            var pump = await _pumpSnapshotRepository.GetLatestAsync(asOf: null, ct);
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
                var pumpFresh = (now - pump.Timestamp) < PumpFreshnessThreshold;
                if (pumpFresh)
                {
                    var span = await _stateSpanService.GetActiveAtAsync(
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
            var temp = await _tempBasalRepository.GetActiveAtAsync(now, ct);
            if (temp is not null)
            {
                enriched = enriched with { ActiveTempBasal = ProjectTempBasal(temp) };
            }
        }

        if (needs.NeedsUploaderStatus)
        {
            var uploader = await _uploaderSnapshotRepository.GetLatestAsync(asOf: null, ct);
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
            var span = await _stateSpanService.GetActiveAtAsync(
                StateSpanCategory.Override, state: null, at: now, ct);
            if (span is not null)
            {
                var multiplier = TryReadDecimal(span.Metadata, "insulinNeedsScaleFactor");
                var name = TryReadString(span.Metadata, "reasonDisplay");
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

    private static decimal? TryReadDecimal(Dictionary<string, object>? metadata, string key)
    {
        if (metadata is null) return null;
        if (!metadata.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            decimal d => d,
            double dbl when double.IsFinite(dbl) => (decimal)dbl,
            float f when float.IsFinite(f) => (decimal)f,
            int i => i,
            long l => l,
            string s when decimal.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
            System.Text.Json.JsonElement je => je.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number when je.TryGetDecimal(out var dec) => dec,
                System.Text.Json.JsonValueKind.String when decimal.TryParse(
                    je.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => (decimal?)null,
            },
            _ => null,
        };
    }

    private static string? TryReadString(Dictionary<string, object>? metadata, string key)
    {
        if (metadata is null) return null;
        if (!metadata.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            string s => s,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String => je.GetString(),
            _ => v.ToString(),
        };
    }

    private async Task<List<Treatment>> FetchRecentTreatmentsAsync(CancellationToken ct)
    {
        var nowMills = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var cutoffMills = nowMills - (TreatmentLookbackHours * 60L * 60L * 1000L);

        var page = await _treatmentService.GetTreatmentsAsync(TreatmentFetchLimit, 0, ct);
        return page.Where(t => t.Mills >= cutoffMills).ToList();
    }

    private async Task<decimal?> ComputeIobAsync(List<Treatment> treatments, CancellationToken ct)
    {
        try
        {
            var result = await _iobService.CalculateTotalAsync(treatments, ct: ct);
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
            var result = await _cobService.CobTotalAsync(treatments, ct: ct);
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

    private async Task<decimal?> FetchReservoirAsync(CancellationToken ct)
    {
        var snapshots = await _pumpSnapshotRepository.GetAsync(
            from: null, to: null, device: null, source: null,
            limit: 1, offset: 0, descending: true, ct: ct);

        var reservoir = snapshots.FirstOrDefault()?.Reservoir;
        return reservoir is null ? null : (decimal)reservoir.Value;
    }

    private async Task<DateTime?> FetchLatestEventAsync(DeviceEventType eventType, CancellationToken ct)
    {
        var evt = await _deviceEventRepository.GetLatestByEventTypeAsync(eventType, ct);
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
