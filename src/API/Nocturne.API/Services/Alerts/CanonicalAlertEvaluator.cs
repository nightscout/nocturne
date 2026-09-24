using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Default <see cref="ICanonicalAlertEvaluator"/>: resolves the latest canonical reading via
/// <see cref="ICanonicalGlucoseService"/> and hands a <see cref="SensorContext"/> to the
/// <see cref="IAlertOrchestrator"/>.
/// </summary>
/// <remarks>
/// Callers publish in chunks and re-publish readings they have already stored, so the same
/// canonical reading arrives here many times over; <see cref="AlertEvaluationWatermark"/> collapses
/// those repeats to one pass per reading per
/// <see cref="AlertEvaluationWatermark.MaxSkipWindow"/>. Hysteresis closure, snooze expiry,
/// auto-resolve and rules containing a <see cref="WallClockConditions"/> kind are also driven by
/// the timer in <see cref="AlertSweepService"/>.
/// </remarks>
internal sealed class CanonicalAlertEvaluator : ICanonicalAlertEvaluator
{
    private readonly ICanonicalGlucoseService _canonicalGlucose;
    private readonly IAlertOrchestrator _alertOrchestrator;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly AlertEvaluationWatermark _watermark;
    private readonly ILogger<CanonicalAlertEvaluator> _logger;

    public CanonicalAlertEvaluator(
        ICanonicalGlucoseService canonicalGlucose,
        IAlertOrchestrator alertOrchestrator,
        ITenantAccessor tenantAccessor,
        AlertEvaluationWatermark watermark,
        ILogger<CanonicalAlertEvaluator> logger)
    {
        _canonicalGlucose = canonicalGlucose ?? throw new ArgumentNullException(nameof(canonicalGlucose));
        _alertOrchestrator = alertOrchestrator ?? throw new ArgumentNullException(nameof(alertOrchestrator));
        _tenantAccessor = tenantAccessor ?? throw new ArgumentNullException(nameof(tenantAccessor));
        _watermark = watermark ?? throw new ArgumentNullException(nameof(watermark));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// The base context an evaluation pass starts from for <paramref name="latest"/>, the
    /// tenant's newest canonical reading; the orchestrator's enricher fills in the rest.
    /// </summary>
    internal static SensorContext ContextFor(SensorGlucose latest) => new()
    {
        LatestValue = (decimal)latest.Mgdl,
        LatestTimestamp = latest.Timestamp,
        TrendRate = (decimal?)latest.TrendRate,
        LastReadingAt = latest.Timestamp,
    };

    /// <inheritdoc />
    public async Task EvaluateAsync(CancellationToken ct = default)
    {
        try
        {
            var latest = await _canonicalGlucose.GetLatestAsync(ct);
            if (latest is null || latest.Mgdl <= 0) return;

            var tenantId = _tenantAccessor.TenantId;
            if (_watermark.AlreadyEvaluated(tenantId, latest))
            {
                _logger.LogTrace(
                    "Alert evaluation skipped for tenant {TenantId}: reading at {ReadingTimestamp} was already evaluated",
                    tenantId, latest.Timestamp);
                return;
            }

            await _alertOrchestrator.EvaluateAsync(ContextFor(latest), ct);

            _watermark.Record(tenantId, latest);
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
    }
}
