using Nocturne.API.Services.Analytics;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Mcp.Reports.Datasets;

/// <summary>
/// What one report run has read, by range, so panels over the same range share one read: an AGP's
/// three glucose panels read and canonicalise the readings once, and the audit counts them once.
/// One per run (<see cref="ReportRunContext.Cache"/>); the panels run in turn, so it is not locked.
/// </summary>
internal sealed class ReportRunCache
{
    private const DiabetesPopulation Population = DiabetesPopulation.Type1Adult;

    private readonly Dictionary<(long From, long To), IReadOnlyList<SensorGlucose>> _glucose = [];
    private readonly Dictionary<(long From, long To), ExtendedGlucoseAnalytics> _analysis = [];

    /// <summary>The canonical readings over the request's range, read and recorded for the audit the first time only.</summary>
    public async Task<IReadOnlyList<SensorGlucose>> GlucoseAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var key = Key(request);
        if (_glucose.TryGetValue(key, out var cached))
            return cached;

        var services = request.Services;
        var raw = await services.GetRequiredService<ISensorGlucoseRepository>().GetForAnalyticsAsync(
            request.From.UtcDateTime, request.To.UtcDateTime, null, null, int.MaxValue, descending: false, ct: cancellationToken);
        var entries = await services.GetRequiredService<ICanonicalGlucoseService>().SelectAsync(raw.ToList(), cancellationToken);
        if (entries.Count > 0)
            request.ReadLog.Record("glucose", entries.Count);
        _glucose[key] = entries;
        return entries;
    }

    /// <summary>
    /// The readings' analysis as <c>GET /api/v4/statistics/range-analytics</c> computes it, once per range.
    /// </summary>
    public async Task<ExtendedGlucoseAnalytics> AnalysisAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var key = Key(request);
        if (_analysis.TryGetValue(key, out var cached))
            return cached;

        var entries = await GlucoseAsync(request, cancellationToken);
        var clock = await request.Services.GetRequiredService<TenantClockResolver>().ResolveAsync(cancellationToken);
        var analysis = request.Services.GetRequiredService<IStatisticsService>().AnalyzeGlucoseDataExtended(
            entries, [], [], StatisticsService.WindowDays(request.From.UtcDateTime, request.To.UtcDateTime), Population, timeZone: clock.Zone);
        _analysis[key] = analysis;
        return analysis;
    }

    private static (long, long) Key(ReportDatasetRequest request) =>
        (request.From.ToUnixTimeMilliseconds(), request.To.ToUnixTimeMilliseconds());
}
