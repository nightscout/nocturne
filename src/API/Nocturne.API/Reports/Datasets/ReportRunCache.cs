using Nocturne.API.Authorization;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// What one report run has read, so a query is read once however many panels, layers and derived
/// sources name it: a dataset query by its key (<see cref="ReportReadKey"/>), and below that the
/// readings and the dashboard chart payload by range, which several datasets share. The audit counts
/// each read once. One per run (<see cref="ReportRunContext.Cache"/>); the layers run in turn, so it
/// is not locked.
/// </summary>
internal sealed class ReportRunCache
{
    private const DiabetesPopulation Population = DiabetesPopulation.Type1Adult;

    /// <summary>The interval the chart payload's IOB, COB and basal series are computed at.</summary>
    private const int ChartIntervalMinutes = 5;

    private readonly Dictionary<string, Task<ReportDatasetOutput>> _reads = new(StringComparer.Ordinal);
    private readonly HashSet<string> _counted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<EventResponseDataset.EventRun>> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<(long From, long To), IReadOnlyList<SensorGlucose>> _glucose = [];
    private readonly Dictionary<(long From, long To), ExtendedGlucoseAnalytics> _analysis = [];
    private readonly Dictionary<(long From, long To), DashboardChartData> _chart = [];

    /// <summary>The dataset reads made so far, reads that share their costly part (<see cref="IReportDataset.SharedRead"/>) once.</summary>
    public int Reads => _counted.Count;

    /// <summary>Whether the read <paramref name="key"/> names has been made, so a run past its deadline still draws what it read.</summary>
    public bool Has(string key) => _reads.TryGetValue(key, out var task) && task.IsCompletedSuccessfully;

    /// <summary>
    /// The output of the read <paramref name="key"/> names, read the first time it is asked for. A read
    /// that failed or ran out of time stays so for every layer that names it; it is not read again.
    /// </summary>
    /// <param name="countKey">The key the read counts under (<see cref="ReportSpecValidator.CountKey"/>).</param>
    public Task<ReportDatasetOutput> ReadAsync(string key, string countKey, Func<Task<ReportDatasetOutput>> read)
    {
        _counted.Add(countKey);
        if (!_reads.TryGetValue(key, out var task))
        {
            task = read();
            _reads[key] = task;
        }
        return task;
    }

    /// <summary>
    /// The event groups' analysis for <paramref name="key"/>, computed and audited the first time only: every
    /// event dataset over the same groups and range reads it (<see cref="EventResponseDataset.ComputeAsync"/>).
    /// </summary>
    public Task<EventResponseDataset.EventRun> EventsAsync(string key, Func<Task<EventResponseDataset.EventRun>> compute)
    {
        if (!_events.TryGetValue(key, out var task))
        {
            task = compute();
            _events[key] = task;
        }
        return task;
    }

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

    /// <summary>
    /// The dashboard chart's payload over the request's range, without the health series, redacted
    /// for the caller as its endpoint redacts (<see cref="ChartDataReadScopeGuard"/>). Nothing is
    /// recorded here: each dataset records what it serves of it.
    /// </summary>
    public async Task<DashboardChartData> ChartAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var key = Key(request);
        if (_chart.TryGetValue(key, out var cached))
            return cached;

        var data = await request.Services.GetRequiredService<IChartDataService>().GetDashboardChartDataAsync(
            request.From.ToUnixTimeMilliseconds(), request.To.ToUnixTimeMilliseconds(), ChartIntervalMinutes,
            includeHealthSeries: false, cancellationToken);
        data = ChartDataReadScopeGuard.Redact(data, request.GrantedScopes);
        data.TrackerMarkers = [];
        _chart[key] = data;
        return data;
    }

    private static (long, long) Key(ReportDatasetRequest request) =>
        (request.From.ToUnixTimeMilliseconds(), request.To.ToUnixTimeMilliseconds());
}
