using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Default <see cref="ICanonicalAlertEvaluator"/>: resolves the latest canonical reading via
/// <see cref="ICanonicalGlucoseService"/> and hands a <see cref="SensorContext"/> to the
/// <see cref="IAlertOrchestrator"/>.
/// </summary>
/// <remarks>
/// Callers publish in chunks and re-publish readings they have already stored, so the same
/// canonical reading arrives here many times over; <see cref="AlertEvaluationWatermark"/> keeps
/// the orchestrator pass to once per reading. Rules that turn on elapsed time rather than on a
/// new reading — signal loss, hysteresis closure, snooze expiry, auto-resolve, tracker age — are
/// owned by <see cref="AlertSweepService"/>'s own timer and are unaffected by that skip.
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

    /// <inheritdoc />
    public async Task EvaluateAsync(CancellationToken ct = default)
    {
        try
        {
            var latest = await _canonicalGlucose.GetLatestAsync(ct);
            if (latest is null || latest.Mgdl <= 0) return;

            // Guid.Empty means no tenant is in scope; the orchestrator returns before its first
            // read in that case, so there is nothing for the watermark to save.
            var tenantId = _tenantAccessor.TenantId;
            if (tenantId != Guid.Empty && _watermark.AlreadyEvaluated(tenantId, latest))
                return;

            var context = new SensorContext
            {
                LatestValue = (decimal)latest.Mgdl,
                LatestTimestamp = latest.Timestamp,
                TrendRate = (decimal?)latest.TrendRate,
                LastReadingAt = latest.Timestamp,
            };

            await _alertOrchestrator.EvaluateAsync(context, ct);

            if (tenantId != Guid.Empty)
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
