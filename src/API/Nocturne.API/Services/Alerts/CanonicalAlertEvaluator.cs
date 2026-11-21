using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Default <see cref="ICanonicalAlertEvaluator"/>: selects the reading an alert context is built
/// from (<see cref="AlertReadings"/>) and hands a <see cref="SensorContext"/> to the
/// <see cref="IAlertOrchestrator"/>.
/// </summary>
/// <remarks>
/// A reading dated beyond the clock tolerance is not selected, so its arrival evaluates nothing new:
/// the selected reading is the one evaluated before it, and the watermark skips it as such. The
/// arrival is reported to <see cref="IClockAnomalyNotice"/> after the evaluation, whatever the
/// watermark says, and a failure of the notice is only logged.
/// Callers publish in chunks and re-publish readings they have already stored, so the same
/// canonical reading arrives here many times over; <see cref="AlertEvaluationWatermark"/> collapses
/// those repeats to one pass per reading per
/// <see cref="AlertEvaluationWatermark.MaxSkipWindow"/>. Hysteresis closure, snooze expiry,
/// auto-resolve and wall-clock rules (<see cref="Engines.RuleTrees.ReferencesWallClock"/>) are also driven by
/// the timer in <see cref="AlertSweepJob"/>.
/// </remarks>
internal sealed class CanonicalAlertEvaluator : ICanonicalAlertEvaluator
{
    private readonly ICanonicalGlucoseService _canonicalGlucose;
    private readonly IAlertOrchestrator _alertOrchestrator;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly AlertEvaluationWatermark _watermark;
    private readonly IClockAnomalyNotice _clockAnomalyNotice;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CanonicalAlertEvaluator> _logger;

    public CanonicalAlertEvaluator(
        ICanonicalGlucoseService canonicalGlucose,
        IAlertOrchestrator alertOrchestrator,
        ITenantAccessor tenantAccessor,
        AlertEvaluationWatermark watermark,
        IClockAnomalyNotice clockAnomalyNotice,
        TimeProvider timeProvider,
        ILogger<CanonicalAlertEvaluator> logger)
    {
        _canonicalGlucose = canonicalGlucose ?? throw new ArgumentNullException(nameof(canonicalGlucose));
        _alertOrchestrator = alertOrchestrator ?? throw new ArgumentNullException(nameof(alertOrchestrator));
        _tenantAccessor = tenantAccessor ?? throw new ArgumentNullException(nameof(tenantAccessor));
        _watermark = watermark ?? throw new ArgumentNullException(nameof(watermark));
        _clockAnomalyNotice = clockAnomalyNotice ?? throw new ArgumentNullException(nameof(clockAnomalyNotice));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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
    };

    /// <inheritdoc />
    public async Task EvaluateAsync(CancellationToken ct = default)
    {
        try
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var selection = await AlertReadings.SelectAsync(_canonicalGlucose, now, ct);
            await EvaluateSelectedAsync(selection.Selected, ct);
            await _clockAnomalyNotice.ReportReadingIsolatedAsync(selection, now, _logger, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Alert evaluation against the canonical stream failed");
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Alert evaluation against the canonical stream failed");
        }
        catch (Exception ex)
        {
            // Runs inline in glucose ingestion: the reading is already stored, so a fault here
            // must not fail the write that triggered it.
            _logger.LogError(ex, "Alert evaluation against the canonical stream failed unexpectedly");
        }
    }

    private async Task EvaluateSelectedAsync(SensorGlucose? latest, CancellationToken ct)
    {
        if (latest is null || latest.Mgdl <= 0) return;

        var tenantId = _tenantAccessor.TenantId;
        if (_watermark.AlreadyEvaluated(tenantId, latest))
        {
            _logger.LogTrace(
                "Alert evaluation skipped for tenant {TenantId}: reading at {ReadingTimestamp} was already evaluated",
                tenantId, latest.Timestamp);
            return;
        }

        var trend = await ReadingTrend.ForAsync(latest, _canonicalGlucose, ct);
        await _alertOrchestrator.EvaluateAsync(ContextFor(latest, trend), ct);

        _watermark.Record(tenantId, latest);
    }
}
