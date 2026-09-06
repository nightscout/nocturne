using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Default <see cref="ICanonicalAlertEvaluator"/>: resolves the latest canonical reading via
/// <see cref="ICanonicalGlucoseService"/> and hands a <see cref="SensorContext"/> to the
/// <see cref="IAlertOrchestrator"/>.
/// </summary>
internal sealed class CanonicalAlertEvaluator : ICanonicalAlertEvaluator
{
    private readonly ICanonicalGlucoseService _canonicalGlucose;
    private readonly IAlertOrchestrator _alertOrchestrator;
    private readonly ILogger<CanonicalAlertEvaluator> _logger;

    public CanonicalAlertEvaluator(
        ICanonicalGlucoseService canonicalGlucose,
        IAlertOrchestrator alertOrchestrator,
        ILogger<CanonicalAlertEvaluator> logger)
    {
        _canonicalGlucose = canonicalGlucose ?? throw new ArgumentNullException(nameof(canonicalGlucose));
        _alertOrchestrator = alertOrchestrator ?? throw new ArgumentNullException(nameof(alertOrchestrator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task EvaluateAsync(CancellationToken ct = default)
    {
        try
        {
            var latest = await _canonicalGlucose.GetLatestAsync(ct);
            if (latest is null || latest.Mgdl <= 0) return;

            var context = new SensorContext
            {
                LatestValue = (decimal)latest.Mgdl,
                LatestTimestamp = latest.Timestamp,
                TrendRate = (decimal?)latest.TrendRate,
                LastReadingAt = latest.Timestamp,
            };

            await _alertOrchestrator.EvaluateAsync(context, ct);
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
