using Nocturne.API.Authorization;
using Nocturne.API.Reports;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Widget;

namespace Nocturne.API.Services.Platform;

/// <summary>A clock face's chart window and, when one of its charts draws a forecast, the forecast.</summary>
public class ClockFaceChart
{
    /// <summary>
    /// The dashboard chart payload, emptied of every track no chart on the face draws; null when the
    /// face has no chart its reader may be served.
    /// </summary>
    public DashboardChartData? Chart { get; set; }

    /// <summary>Null when no chart draws a forecast, or there is none to draw.</summary>
    public GlucosePredictionResponse? Predictions { get; set; }
}

/// <summary>
/// Reads what a clock face's elements show, for a reader holding a given set of scopes: a face's
/// owner when its link is opened, or the editor previewing a layout not yet saved. Only the parts
/// the layout holds are read, and a part the reader may not read is left out
/// (<see cref="ClockFaceReadScopes"/>).
/// </summary>
public sealed class ClockFaceDataService(
    IIobCobReader iobCob,
    IChartDataService charts,
    ISensorGlucoseRepository glucose,
    IStatisticsService statistics,
    ITrackerRepository trackers,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<ClockFaceDataService> logger,
    IPredictionService? predictions = null)
{
    private const long HourMs = 60 * 60 * 1000;

    /// <summary>Pumps step basal in hundredths of a unit per hour.</summary>
    private const int BasalRateDigits = 2;

    /// <summary>How far back the basal series is read to find the rate in force now.</summary>
    private const int BasalLookbackHours = 6;

    /// <summary>Readings in a day at one every five minutes, with room for a faster sensor.</summary>
    private const int MaxSummaryReadings = 24 * 60;

    /// <summary>The values the face's text and tracker elements show.</summary>
    public async Task<ClockFaceData> ReadAsync(
        ClockFaceConfig config, IReadOnlySet<string> held, CancellationToken ct)
    {
        var elements = Served(config, held).ToList();
        var types = elements.Select(e => e.Type).ToHashSet();
        var now = time.GetUtcNow();
        var nowMills = now.ToUnixTimeMilliseconds();
        var data = new ClockFaceData();

        if (types.Contains("iob") || types.Contains("cob"))
        {
            var reading = await iobCob.ReadTotalAsync(nowMills, live: true, ct);
            var onBoard = OnBoardFigures.Of(reading);
            if (types.Contains("iob"))
                data.Iob = onBoard.Insulin;
            if (types.Contains("cob"))
                data.Cob = onBoard.Carbs;
        }

        if (types.Contains("basal"))
        {
            var series = await charts.GetBasalSeriesAsync(nowMills - BasalLookbackHours * HourMs, nowMills, ct);
            if (series.Where(p => p.Timestamp <= nowMills).MaxBy(p => p.Timestamp) is { } current)
            {
                data.Basal = new ClockBasalDto
                {
                    Rate = ScaledRounding.Round(current.Rate, BasalRateDigits),
                    ScheduledRate = current.ScheduledRate is { } scheduled
                        ? ScaledRounding.Round(scheduled, BasalRateDigits)
                        : null,
                    Origin = current.Origin,
                };
            }
        }

        var minutesAhead = elements.Where(e => e.Type == "forecast")
            .Select(e => Offered(e.MinutesAhead, ClockElement.OfferedMinutesAhead, ClockElement.DefaultMinutesAhead))
            .Distinct().ToList();
        if (minutesAhead.Count > 0 && await PredictAsync(ct) is { } forecast)
            data.Forecasts = [.. minutesAhead.Select(m => At(forecast, m, now)).OfType<ClockForecastDto>()];

        foreach (var hours in elements.Where(e => e.Type == "summary")
            .Select(e => Offered(e.Hours, ClockElement.OfferedHours, ClockElement.DefaultHours)).Distinct())
        {
            data.Summaries.Add(await SummarizeAsync(hours, now.UtcDateTime, ct));
        }

        var trackerElements = elements.Where(e => e.Type is "tracker" or "trackers").ToList();
        if (trackerElements.Count > 0)
        {
            var running = await trackers.GetActiveInstancesAsync(ct);
            data.Trackers =
            [
                .. TrackerStatusCondenser.All(running, now.UtcDateTime)
                    .Where(s => trackerElements.Any(e => Shows(e, s.Status)))
                    .Select(s => new ClockTrackerDto
                    {
                        DefinitionId = s.Status.DefinitionId,
                        Name = s.Status.Name,
                        Category = s.Status.Category,
                        Urgency = s.Status.Urgency,
                        HoursLeft = s.HoursLeft,
                    }),
            ];
        }

        return data;
    }

    /// <summary>The chart window the face's charts draw, as wide as its widest.</summary>
    public async Task<ClockFaceChart> ReadChartAsync(
        ClockFaceConfig config, IReadOnlySet<string> held, CancellationToken ct)
    {
        var chartConfigs = Served(config, held).Where(e => e.Type == "chart")
            .Select(e => (Hours: Offered(e.Hours, ClockElement.OfferedHours, ClockElement.DefaultHours), Config: e.ChartConfig ?? new ClockChartConfig()))
            .ToList();
        if (chartConfigs.Count == 0)
            return new ClockFaceChart();

        var drawn = chartConfigs.Select(c => c.Config).ToList();
        bool Any(Func<ClockChartConfig, bool> track) => drawn.Any(track);

        var endMills = time.GetUtcNow().ToUnixTimeMilliseconds();
        var startMills = endMills - chartConfigs.Max(c => c.Hours) * HourMs;
        var read = await charts.GetDashboardChartDataAsync(
            startMills, endMills, intervalMinutes: 5, includeHealthSeries: false, ct);

        HashSet<string> granted = [Scope.GlucoseRead];
        if (Any(ClockFaceReadScopes.DrawsTreatments))
            granted.Add(Scope.TreatmentsRead);
        if (Any(c => c.ShowDeviceEvents))
            granted.Add(Scope.DevicesRead);
        read = ChartDataReadScopeGuard.Redact(read, granted);

        return new ClockFaceChart
        {
            Chart = Drawn(read, Any),
            Predictions = Any(c => c.ShowPredictions) ? await PredictAsync(ct) : null,
        };
    }

    /// <summary>
    /// Only the fields the clock's chart tracks draw, copied field by field: anyone holding the
    /// link reads this payload, and the clock draws no tooltip, so labels, notes, record ids and
    /// sources would reach the reader without ever reaching the screen.
    /// </summary>
    private static DashboardChartData Drawn(DashboardChartData read, Func<Func<ClockChartConfig, bool>, bool> any)
    {
        var drawn = new DashboardChartData
        {
            Thresholds = read.Thresholds,
            GlucoseData = [.. read.GlucoseData.Select(p => new GlucosePointDto { Time = p.Time, Sgv = p.Sgv })],
        };

        if (any(c => c.ShowBolus))
        {
            drawn.BolusMarkers =
            [
                .. read.BolusMarkers.Select(m => new BolusMarkerDto
                {
                    Time = m.Time, Insulin = m.Insulin, BolusType = m.BolusType, IsOverride = m.IsOverride,
                }),
            ];
        }

        if (any(c => c.ShowCarbs))
        {
            drawn.CarbMarkers =
            [
                .. read.CarbMarkers.Select(m => new CarbMarkerDto { Time = m.Time, Carbs = m.Carbs, IsOffset = m.IsOffset }),
            ];
        }

        if (any(c => c.ShowIob))
        {
            drawn.IobSeries = read.IobSeries;
            drawn.MinIob = read.MinIob;
            drawn.MaxIob = read.MaxIob;
        }

        if (any(c => c.ShowCob))
        {
            drawn.CobSeries = read.CobSeries;
            drawn.MaxCob = read.MaxCob;
        }

        if (any(c => c.ShowBasal))
        {
            drawn.BasalSeries = read.BasalSeries;
            drawn.DefaultBasalRate = read.DefaultBasalRate;
            drawn.MaxBasalRate = read.MaxBasalRate;
            drawn.LastPumpSyncMills = read.LastPumpSyncMills;
            drawn.TempBasalSpans = [.. read.TempBasalSpans.Select(TempBasalAsDrawn)];
        }

        if (any(c => c.ShowDeviceEvents))
        {
            drawn.DeviceEventMarkers =
            [
                .. read.DeviceEventMarkers.Select(m => new DeviceEventMarkerDto
                {
                    Time = m.Time, EventType = m.EventType, Color = m.Color, IsInferred = m.IsInferred,
                }),
            ];
        }

        if (any(c => c.ShowTrackers))
        {
            drawn.TrackerMarkers =
            [
                .. read.TrackerMarkers.Select(m => new TrackerMarkerDto
                {
                    Id = m.Id, DefinitionId = m.DefinitionId, Name = m.Name, Category = m.Category,
                    Time = m.Time, Icon = m.Icon, Color = m.Color,
                }),
            ];
        }

        return drawn;
    }

    /// <summary>The metadata keys the basal track reads a temp basal's rate from.</summary>
    private static readonly string[] TempBasalRateKeys = ["rate", "absolute", "percent"];

    private static ChartStateSpanDto TempBasalAsDrawn(ChartStateSpanDto span) => new()
    {
        Id = span.Id,
        Kind = span.Kind,
        Category = span.Category,
        State = span.State,
        StartMills = span.StartMills,
        EndMills = span.EndMills,
        Color = span.Color,
        Metadata = span.Metadata?
            .Where(kv => TempBasalRateKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value),
    };

    /// <summary>
    /// The urgency a tracker element's visibility threshold asks for, as the editor offers it;
    /// null for "always" and for a threshold it does not offer.
    /// </summary>
    private static readonly Dictionary<string, NotificationUrgency> ThresholdUrgency = new(StringComparer.Ordinal)
    {
        ["info"] = NotificationUrgency.Info,
        ["warn"] = NotificationUrgency.Warn,
        ["hazard"] = NotificationUrgency.Hazard,
        ["urgent"] = NotificationUrgency.Urgent,
    };

    /// <summary>
    /// Whether a tracker element shows a running tracker: the one it names, or one in its categories
    /// (every category when it names none), at or above its visibility threshold.
    /// </summary>
    internal static bool Shows(ClockElement element, V4TrackerStatus tracker)
    {
        var chosen = element.Type == "tracker"
            ? element.DefinitionId == tracker.DefinitionId
            : element.Categories is not { Count: > 0 } categories
              || categories.Contains(tracker.Category.ToString(), StringComparer.Ordinal);
        return chosen
            && (element.VisibilityThreshold is not { } threshold
                || !ThresholdUrgency.TryGetValue(threshold, out var least)
                || tracker.Urgency >= least);
    }

    private static IEnumerable<ClockElement> Served(ClockFaceConfig config, IReadOnlySet<string> held) =>
        config.Rows.SelectMany(r => r.Elements).Where(e => ClockFaceReadScopes.Allows(held, e));

    /// <summary>A face saved before its values were checked can hold one the editor never offered.</summary>
    private static int Offered(int? value, IReadOnlyList<int> offered, int fallback) =>
        value is { } v && offered.Contains(v) ? v : fallback;

    private async Task<GlucosePredictionResponse?> PredictAsync(CancellationToken ct)
    {
        if (predictions is null || PredictionOptions.ResolveSource(configuration) == PredictionSource.None)
            return null;

        try
        {
            return await predictions.GetPredictionsAsync(profileId: null, asOf: null, ct);
        }
        catch (InvalidOperationException ex)
        {
            // The source has no forecast to give: the normal state for a tenant without a loop.
            logger.LogDebug(ex, "No forecast for a clock face");
            return null;
        }
    }

    /// <summary>
    /// The forecast's main curve <paramref name="minutesAhead"/> from <paramref name="now"/>, if it
    /// reaches that far. The curve starts at <see cref="GlucosePredictionResponse.Timestamp"/>, which a
    /// loop's upload can leave minutes behind now.
    /// </summary>
    private static ClockForecastDto? At(GlucosePredictionResponse forecast, int minutesAhead, DateTimeOffset now)
    {
        var curve = forecast.Predictions.Default;
        var interval = forecast.IntervalMinutes > 0 ? forecast.IntervalMinutes : 5;
        var minutesFromStart = Math.Max(0, (now - forecast.Timestamp).TotalMinutes) + minutesAhead;
        var index = (int)Math.Round(minutesFromStart / interval, MidpointRounding.AwayFromZero);
        return curve is not null && index < curve.Count
            ? new ClockForecastDto { MinutesAhead = minutesAhead, Mgdl = curve[index] }
            : null;
    }

    private async Task<ClockSummaryDto> SummarizeAsync(int hours, DateTime nowUtc, CancellationToken ct)
    {
        var readings = (await glucose.GetAsync(
            from: nowUtc.AddHours(-hours), to: nowUtc, device: null, source: null,
            limit: MaxSummaryReadings, offset: 0, descending: true, ct: ct)).ToList();
        var percentages = statistics.CalculateTimeInRange(readings).Percentages;
        return new ClockSummaryDto
        {
            Hours = hours,
            InRangePercent = percentages.Target,
            BelowPercent = percentages.VeryLow + percentages.Low,
            AbovePercent = percentages.High + percentages.VeryHigh,
            Readings = readings.Count,
        };
    }
}
