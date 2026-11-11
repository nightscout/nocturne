using System.Globalization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// Heart rate over time from the minute averages the activity report reads
/// (<see cref="IHeartRateService.GetHeartRateMinuteAveragesByDateRangeAsync"/>), one row per slot that
/// holds any: their mean, lowest and highest.
/// </summary>
/// <remarks>
/// Read through the request's own service, so RLS decides what a reader sees: <c>heart_rates</c> is
/// shared under <c>heartrate.read</c> and clamped to the last 24 hours like every time series
/// (<see cref="ShareDataCategories"/>).
/// </remarks>
internal sealed class HeartRateDataset : IReportDataset
{
    private const int Budget = 1500;

    public string Name => HeartRateParameters.Name;

    public string Label => "Heart rate";

    public string Description => "Heart rate over time, in beats a minute.";

    public Type ParametersType => typeof(HeartRateParameters);

    public string RequiredScope => Scope.HeartRateRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("time", "Time", ReportFieldKind.Time, null, "Start of the slot, Unix milliseconds."),
        new("bpm", "Heart rate", ReportFieldKind.Number, "bpm", "The mean of the minute averages in the slot.", Color: ChartColor.HeartRate, Aggregate: ReportAggregate.Mean),
        new("bpmMin", "Heart rate, lowest", ReportFieldKind.Number, "bpm", "The lowest minute average in the slot.", Aggregate: ReportAggregate.Mean),
        new("bpmMax", "Heart rate, highest", ReportFieldKind.Number, "bpm", "The highest minute average in the slot.", Aggregate: ReportAggregate.Max),
    ];

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Line, ReportMark.Band, ReportMark.Points];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) => mark switch
    {
        ReportMark.Band => new ReportLayerBindings { X = "time", Y = ["bpm"], Edges = ["bpmMin", "bpmMax"] },
        ReportMark.Line or ReportMark.Points => new ReportLayerBindings { X = "time", Y = ["bpm"] },
        _ => null,
    };

    public TimeSpan DefaultRange => TimeSpan.FromDays(1);

    public TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => Budget;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var minutes = (await request.Services.GetRequiredService<IHeartRateService>().GetHeartRateMinuteAveragesByDateRangeAsync(
            request.From.UtcDateTime, request.To.UtcDateTime, cancellationToken)).Where(h => h.Bpm > 0).ToList();
        if (minutes.Count > 0)
            request.ReadLog.Record("heart_rates", minutes.Count);

        var interval = GlucoseSeriesDataset.IntervalFor(request.From, request.To, 1, Budget);
        var rows = Slots(minutes.Select(h => (Mills: new DateTimeOffset(DateTime.SpecifyKind(h.Timestamp, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), Value: (double)h.Bpm)),
                request.From.ToUnixTimeMilliseconds(), interval)
            .Select(s => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["time"] = s.Start,
                ["bpm"] = Math.Round(s.Values.Average(), 1),
                ["bpmMin"] = s.Values.Min(),
                ["bpmMax"] = s.Values.Max(),
            }).ToList();

        var context = $"{ReportFacts.Window(request.From, request.To, request.Zone)}. {ReportFacts.Plural(minutes.Count, "minute")} with a heart rate.";
        if (minutes.Count == 0)
            return new ReportDatasetOutput { Rows = [], Summary = "No heart rate in this period.", Sentence = "No heart rate in this period.", Context = context };

        var values = minutes.Select(m => (double)m.Bpm).ToList();
        var mean = values.Average();
        string B(double v) => Number(v, "0");
        var sentence = $"Heart rate: mean {B(mean)} bpm, from {B(values.Min())} to {B(values.Max())} bpm, over {ReportFacts.Plural(minutes.Count, "minute")}.";
        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = sentence + (interval > 1 ? $"\nEach row is {interval} minutes." : ""),
            Facts =
            [
                new ReportFact("Mean", B(mean), "bpm", About: ["bpm"]),
                new ReportFact("Lowest", B(values.Min()), "bpm", "minute average", ["bpm|bpmMin"]),
                new ReportFact("Highest", B(values.Max()), "bpm", "minute average", ["bpm|bpmMax"]),
                ReportFacts.Count("Minutes", minutes.Count).Of("bpm"),
            ],
            Sentence = sentence,
            SentenceAbout = ["bpm"],
            Context = context,
            Resolution = new ReportResolution(1, interval, minutes.Count, rows.Count, interval > 1),
        };
    }

    /// <summary>The values in each <paramref name="interval"/>-minute slot counted from <paramref name="from"/>, slots with none left out.</summary>
    internal static List<(long Start, List<double> Values)> Slots(IEnumerable<(long Mills, double Value)> values, long from, int interval)
    {
        var width = interval * LocalSlotBinning.MinuteMs;
        return
        [
            .. values.GroupBy(v => from + (long)Math.Floor((v.Mills - from) / (double)width) * width)
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Select(v => v.Value).ToList())),
        ];
    }

    private static string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}

/// <summary>
/// Steps over time, added up in each slot, as the dashboard adds them: a record that may be a running
/// counter (<see cref="StepCount.IsPossibleRunningTotal"/>) is left out. Read through the request's own
/// service, so RLS decides what a reader sees: <c>step_counts</c> is shared under <c>stepcount.read</c>
/// and clamped like every time series.
/// </summary>
internal sealed class StepsDataset : IReportDataset
{
    private const int Budget = 1500;

    /// <summary>The finest slot steps are added up in: a record often covers a quarter of an hour.</summary>
    private const int MinimumMinutes = 15;

    /// <summary>The slot widths, so a bar is a part of the clock: the finest one that keeps the rows within the budget.</summary>
    private static readonly int[] SlotMinutes = [15, 30, 60, 120, 180, 360, 720, 1440];

    public string Name => StepsParameters.Name;

    public string Label => "Steps";

    public string Description => "Steps over time, added up in each slot.";

    public Type ParametersType => typeof(StepsParameters);

    public string RequiredScope => Scope.StepCountRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("time", "Time", ReportFieldKind.Time, null, "Start of the slot, Unix milliseconds."),
        new("steps", "Steps", ReportFieldKind.Number, "steps", "The steps recorded in the slot.", Color: ChartColor.Steps, Aggregate: ReportAggregate.Sum),
    ];

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Bars, ReportMark.Line];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) =>
        mark is ReportMark.Bars or ReportMark.Line ? new ReportLayerBindings { X = "time", Y = ["steps"] } : null;

    public TimeSpan DefaultRange => TimeSpan.FromDays(7);

    public TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => Budget;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var records = (await request.Services.GetRequiredService<IStepCountService>().GetStepCountsByDateRangeAsync(
                request.From.UtcDateTime, request.To.UtcDateTime, cancellationToken: cancellationToken))
            .Where(s => s.Metric > 0 && !s.IsPossibleRunningTotal()).ToList();
        if (records.Count > 0)
            request.ReadLog.Record("step_counts", records.Count);

        var needed = GlucoseSeriesDataset.IntervalFor(request.From, request.To, MinimumMinutes, Budget);
        var interval = SlotMinutes.First(m => m >= needed || m == SlotMinutes[^1]);
        var slots = HeartRateDataset.Slots(
            records.Select(s => (new DateTimeOffset(DateTime.SpecifyKind(s.Timestamp, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), (double)s.Metric)),
            request.From.ToUnixTimeMilliseconds(), interval);
        var rows = slots.Select(s => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["time"] = s.Start,
            ["steps"] = s.Values.Sum(),
        }).ToList();

        var total = records.Sum(r => r.Metric);
        var context = $"{ReportFacts.Window(request.From, request.To, request.Zone)}. {ReportFacts.Plural(records.Count, "step record")}.";
        if (records.Count == 0)
            return new ReportDatasetOutput { Rows = [], Summary = "No steps in this period.", Sentence = "No steps in this period.", Context = context };

        var most = slots.MaxBy(s => s.Values.Sum());
        var slotWords = interval % 60 == 0 ? ReportFacts.Plural(interval / 60, "hour") : $"{interval} minutes";
        var sentence = $"Steps: {total.ToString("N0", CultureInfo.InvariantCulture)} in the period; the most in one slot of {slotWords}, "
            + $"{most.Values.Sum().ToString("N0", CultureInfo.InvariantCulture)}, from {ReportFacts.Instant(DateTimeOffset.FromUnixTimeMilliseconds(most.Start), request.Zone)}.";
        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = sentence,
            Facts =
            [
                ReportFacts.Count("Total", total, "steps").Of("steps"),
                new ReportFact("Most in a slot", most.Values.Sum().ToString("0", CultureInfo.InvariantCulture), "steps",
                    $"{slotWords}, from {ReportFacts.Instant(DateTimeOffset.FromUnixTimeMilliseconds(most.Start), request.Zone)}", ["steps"]),
            ],
            Sentence = sentence,
            SentenceAbout = ["steps"],
            Context = context,
            Resolution = new ReportResolution(MinimumMinutes, interval, records.Count, rows.Count, interval > MinimumMinutes),
        };
    }
}

/// <summary>
/// Each sleep session: as a span in a lane, or as hours of sleep on the local night it ended. The heart
/// rate, HRV, breathing and oxygen figures a session carries are not served: they would show a heart
/// rate to a reader without <c>heartrate.read</c>. Read through the request's own repository, so RLS
/// decides what a reader sees: <c>sleep_sessions</c> is hidden from shares and clamped for a member by
/// its end (<see cref="ShareDataCategories.HiddenSpanEndColumns"/>).
/// </summary>
internal sealed class SleepDataset : IReportDataset
{
    /// <summary>The most sessions a panel reads: more than one a night over the longest range.</summary>
    private const int MaxSessions = 1000;

    public string Name => SleepParameters.Name;

    public string Label => "Sleep";

    public string Description => "Each sleep session, as a span or as hours of sleep on the night it ended.";

    public Type ParametersType => typeof(SleepParameters);

    public string RequiredScope => Scope.SleepRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("start", "Start", ReportFieldKind.Time, null, "When the session started, Unix milliseconds."),
        new("end", "End", ReportFieldKind.Time, null, "When the session ended, Unix milliseconds."),
        new("kind", "Kind", ReportFieldKind.Category, null, "Overnight, nap, rest or unknown.", ColorField: "color"),
        new("night", "Night", ReportFieldKind.Day, null, "The local day the session ended: its midnight, Unix milliseconds."),
        new("totalSleep", "Sleep", ReportFieldKind.Number, "h", "Hours asleep in the session.", Color: ChartColor.ActivitySleep, Aggregate: ReportAggregate.Sum),
        new("deepSleep", "Deep sleep", ReportFieldKind.Number, "h", "Hours of deep sleep; empty where the source does not tell stages apart.", Aggregate: ReportAggregate.Sum),
        new("lightSleep", "Light sleep", ReportFieldKind.Number, "h", "Hours of light sleep.", Aggregate: ReportAggregate.Sum),
        new("remSleep", "REM sleep", ReportFieldKind.Number, "h", "Hours of REM sleep.", Aggregate: ReportAggregate.Sum),
        new("awake", "Awake", ReportFieldKind.Number, "h", "Hours awake in the session.", Aggregate: ReportAggregate.Sum),
        new("color", "Color", ReportFieldKind.Category, null, "The kind's theme color. It is drawn in this color."),
    ];

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Spans, ReportMark.Bars];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) => mark switch
    {
        ReportMark.Spans => new ReportLayerBindings { Start = "start", End = "end", Category = "kind" },
        ReportMark.Bars => new ReportLayerBindings { X = "night", Y = ["totalSleep"] },
        _ => null,
    };

    public TimeSpan DefaultRange => TimeSpan.FromDays(14);

    public TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => MaxSessions;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var sessions = (await request.Services.GetRequiredService<ISleepSessionRepository>().GetSessionsAsync(
            request.From.UtcDateTime, request.To.UtcDateTime, limit: MaxSessions + 1, descending: false, cancellationToken: cancellationToken)).ToList();
        var cut = sessions.Count > MaxSessions;
        sessions = [.. sessions.Take(MaxSessions)];
        if (sessions.Count > 0)
            request.ReadLog.Record("sleep_sessions", sessions.Count);

        var zone = request.Zone ?? (await request.Services.GetRequiredService<TenantClockResolver>().ResolveAsync(cancellationToken)).Zone;
        static double? Hours(long? ms) => ms is { } v ? Math.Round(v / 3_600_000.0, 2) : null;
        static long Mills(DateTime at) => new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        long? Night(DateTime end) => zone is null ? null
            : LocalSlotBinning.DayStarts(Mills(end), Mills(end) + 1, zone).Starts[0];

        var rows = sessions.Select(s => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["start"] = Mills(s.StartTime),
            ["end"] = Mills(s.EndTime),
            ["kind"] = KindWords(s.Type),
            ["color"] = ReportWire.Name(s.Type == SleepSessionType.Overnight ? ChartColor.ActivitySleep : ChartColor.MutedForeground),
            ["night"] = Night(s.EndTime),
            ["totalSleep"] = Hours(s.TotalSleepMs),
            ["deepSleep"] = Hours(s.DeepSleepMs),
            ["lightSleep"] = Hours(s.LightSleepMs),
            ["remSleep"] = Hours(s.RemSleepMs),
            ["awake"] = Hours(s.TotalAwakeMs),
        }).ToList();

        var context = $"{ReportFacts.Window(request.From, request.To, request.Zone)}. {ReportFacts.Plural(sessions.Count, "sleep session")}.";
        if (sessions.Count == 0)
            return new ReportDatasetOutput { Rows = [], Summary = "No sleep sessions in this period.", Sentence = "No sleep sessions in this period.", Context = context };

        var overnight = sessions.Where(s => s.Type == SleepSessionType.Overnight).Select(s => s.TotalSleepMs / 3_600_000.0).Order().ToList();
        var lines = new List<string> { $"{ReportFacts.Plural(sessions.Count, "sleep session")}, {overnight.Count} of them overnight." };
        var facts = new List<ReportFact> { ReportFacts.Count("Sessions", sessions.Count).Of("totalSleep|start") };
        string? sentence = null;
        if (overnight.Count > 0)
        {
            var median = overnight.Count % 2 == 1 ? overnight[overnight.Count / 2] : (overnight[overnight.Count / 2 - 1] + overnight[overnight.Count / 2]) / 2;
            string H(double h) => h.ToString("0.#", CultureInfo.InvariantCulture);
            sentence = $"Overnight sleep: median {H(median)} h, from {H(overnight[0])} h to {H(overnight[^1])} h over {ReportFacts.Plural(overnight.Count, "night")}.";
            lines.Add(sentence);
            facts.Add(new ReportFact("Median overnight", H(median), "h", About: ["totalSleep"]));
            facts.Add(new ReportFact("Shortest overnight", H(overnight[0]), "h", About: ["totalSleep"]));
            facts.Add(new ReportFact("Longest overnight", H(overnight[^1]), "h", About: ["totalSleep"]));
        }
        if (zone is null)
            lines.Add("The nights are not placed: no time zone is set in the profile.");
        if (cut)
            lines.Add($"Only the first {MaxSessions} sessions are shown.");

        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = string.Join('\n', lines),
            Facts = facts,
            Sentence = sentence ?? lines[0],
            Context = context,
        };
    }

    internal static string KindWords(SleepSessionType type) => type switch
    {
        SleepSessionType.Overnight => "Overnight",
        SleepSessionType.Nap => "Nap",
        SleepSessionType.Rest => "Rest",
        _ => "Unknown",
    };
}
