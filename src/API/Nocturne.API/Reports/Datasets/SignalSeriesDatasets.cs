using Microsoft.EntityFrameworkCore;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Reports.Datasets;

/// <summary>The parts every series dataset here shares: its row shape, its empty output and the tenant's zone.</summary>
internal static class SeriesRows
{
    public static IReadOnlyDictionary<string, object?> Row(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);

    public static double? Round(double? value, int digits) => value is { } v ? ScaledRounding.Round(v, digits) : null;

    public static ReportDatasetOutput Empty(string sentence, string context, IReadOnlyList<ReportOmittedField>? omitted = null) =>
        new() { Rows = [], Summary = sentence, Sentence = sentence, Context = context, Omitted = omitted ?? [] };

    public static async Task<TimeZoneInfo> ZoneAsync(ReportDatasetRequest request, CancellationToken cancellationToken) =>
        request.Zone ?? (await request.Services.GetRequiredService<TenantClockResolver>().ResolveAsync(cancellationToken)).Zone ?? TimeZoneInfo.Utc;

    /// <summary>The local midnight that starts the day holding <paramref name="mills"/>.</summary>
    public static long DayOf(long mills, TimeZoneInfo zone) => LocalSlotBinning.DayStarts(mills, mills + 1, zone).Starts[0];

    public static long Mills(DateTime at) => new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>
    /// One sentence per figure that has values, each about its own field, so a layer of one figure prints its own:
    /// the first is the output's sentence and the rest its other sentences (<see cref="ReportDatasetOutput.OtherSentences"/>).
    /// </summary>
    public static (string? Sentence, IReadOnlyList<string>? About, IReadOnlyList<ReportSentence> Others) Sentences(
        IEnumerable<(string Field, string Words, IReadOnlyList<double> Values, string Unit, string Format)> figures)
    {
        var sentences = figures.Where(f => f.Values.Count > 0)
            .Select(f => new ReportSentence($"{f.Words}: {Spread(f.Values, f.Unit, f.Format)}.", [f.Field])).ToList();
        return sentences.Count == 0 ? (null, null, []) : (sentences[0].Text, sentences[0].About, sentences[1..]);
    }

    /// <summary>"mean 62, from 48 to 75 bpm": a figure's spread in words.</summary>
    public static string Spread(IReadOnlyList<double> values, string unit, string format = "0.#") =>
        $"mean {ReportFacts.Number(values.Average(), format)}, from {ReportFacts.Number(values.Min(), format)} to {ReportFacts.Number(values.Max(), format)}"
        + (unit switch { "" => "", "%" => "%", _ => $" {unit}" });
}

/// <summary>
/// Each sleep session's own figures on the night it ended: its score, efficiency, time to fall asleep and restless
/// periods, and the heart rate, HRV, blood oxygen and breathing rate the wearable measured in it. The four body
/// figures need <c>heartrate.read</c> as well, the scope <see cref="SleepDataset"/> withholds them for. Read through
/// the request's own repository: <c>sleep_sessions</c> is hidden from shares and clamped for a member by its end.
/// </summary>
internal sealed class SleepNightsDataset : IReportDataset
{
    private const int MaxSessions = 1000;

    private static readonly string[] BodyFields = ["avgHeartRate", "minHeartRate", "hrv", "spo2", "breathingRate"];

    public string Name => SleepNightsParameters.Name;

    public string Label => "Sleep figures";

    public string Description => "Each sleep session's score, efficiency, time to fall asleep, restless periods and body figures, on the night it ended.";

    public Type ParametersType => typeof(SleepNightsParameters);

    public string RequiredScope => Scope.SleepRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("night", "Night", ReportFieldKind.Day, null, "The local day the session ended: its midnight, Unix milliseconds."),
        new("score", "Sleep score", ReportFieldKind.Number, null, "The score the source gave the session, 0 to 100.", Color: ChartColor.ActivitySleep, Aggregate: ReportAggregate.Mean),
        new("efficiency", "Sleep efficiency", ReportFieldKind.Percent, "%", "The percent of the session spent asleep.", Color: ChartColor.Chart4, Aggregate: ReportAggregate.Mean),
        new("latency", "Time to fall asleep", ReportFieldKind.Number, "min", "Minutes from the start of the session to sleep.", Color: ChartColor.Chart5, Aggregate: ReportAggregate.Mean),
        new("restless", "Restless periods", ReportFieldKind.Number, null, "How many restless periods the source counted.", Color: ChartColor.Chart3, Aggregate: ReportAggregate.Sum),
        new("avgHeartRate", "Heart rate in sleep", ReportFieldKind.Number, "bpm", "The mean heart rate of the session.", Scope.HeartRateRead, ChartColor.HeartRate, Aggregate: ReportAggregate.Mean),
        new("minHeartRate", "Lowest heart rate in sleep", ReportFieldKind.Number, "bpm", "The lowest heart rate of the session.", Scope.HeartRateRead, Aggregate: ReportAggregate.Min),
        new("hrv", "HRV in sleep", ReportFieldKind.Number, "ms", "The mean heart rate variability of the session.", Scope.HeartRateRead, ChartColor.Chart4, Aggregate: ReportAggregate.Mean),
        new("spo2", "Blood oxygen in sleep", ReportFieldKind.Percent, "%", "The mean blood oxygen (SpO2) of the session.", Scope.HeartRateRead, ChartColor.Chart3, Aggregate: ReportAggregate.Mean),
        new("breathingRate", "Breathing rate in sleep", ReportFieldKind.Number, "breaths/min", "The mean breathing rate of the session.", Scope.HeartRateRead, ChartColor.Chart5, Aggregate: ReportAggregate.Mean),
    ];

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Points, ReportMark.Line, ReportMark.Bars];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) =>
        mark is ReportMark.Points or ReportMark.Line or ReportMark.Bars ? new ReportLayerBindings { X = "night", Y = ["score"] } : null;

    public TimeSpan DefaultRange => TimeSpan.FromDays(30);

    public TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => MaxSessions;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var body = request.HasScope(Scope.HeartRateRead);
        IReadOnlyList<ReportOmittedField> omitted = body ? [] : [.. BodyFields.Select(f => new ReportOmittedField(f, Scope.HeartRateRead))];
        var sessions = (await request.Services.GetRequiredService<ISleepSessionRepository>().GetSessionsAsync(
            request.From.UtcDateTime, request.To.UtcDateTime, limit: MaxSessions, descending: false, cancellationToken: cancellationToken)).ToList();
        if (sessions.Count > 0)
            request.ReadLog.Record("sleep_sessions", sessions.Count);
        var context = $"{ReportFacts.Window(request.From, request.To, request.Zone)}. {ReportFacts.Plural(sessions.Count, "sleep session")}.";
        if (sessions.Count == 0)
            return SeriesRows.Empty("No sleep sessions in this period.", context, omitted);

        var zone = await SeriesRows.ZoneAsync(request, cancellationToken);
        var rows = Rows(sessions, zone, body);
        List<double> Of(Func<SleepSession, double?> value) => [.. sessions.Select(value).OfType<double>()];
        var figures = new List<(string Field, string Words, IReadOnlyList<double> Values, string Unit, string Format)>
        {
            ("score", "Sleep score", Of(s => s.SleepScore), "", "0"),
            ("efficiency", "Sleep efficiency", Of(s => s.Efficiency), "%", "0"),
            ("latency", "Time to fall asleep", Of(s => s.SleepLatencyMs / 60_000.0), "min", "0"),
            ("restless", "Restless periods", Of(s => s.RestlessPeriods), "", "0"),
        };
        if (body)
            figures.AddRange(
            [
                ("avgHeartRate", "Heart rate in sleep", Of(s => s.AvgHeartRate), "bpm", "0"),
                ("minHeartRate", "Lowest heart rate in sleep", Of(s => s.MinHeartRate), "bpm", "0"),
                ("hrv", "HRV in sleep", Of(s => s.AvgHrv), "ms", "0"),
                ("spo2", "Blood oxygen in sleep", Of(s => s.AvgSpo2), "%", "0.#"),
                ("breathingRate", "Breathing rate in sleep", Of(s => s.AvgBreathRate), "breaths/min", "0.#"),
            ]);
        var (sentence, about, others) = SeriesRows.Sentences(figures);
        var lines = new List<string> { $"Sleep figures from {ReportFacts.Window(request.From, request.To, request.Zone)}: {ReportFacts.Plural(sessions.Count, "session")}." };
        lines.AddRange(new[] { sentence }.OfType<string>().Concat(others.Select(o => o.Text)));
        if (!body)
            lines.Add("Heart rate, HRV, blood oxygen and breathing rate are not shown, because the token does not hold the heart rate scope.");
        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = string.Join('\n', lines),
            Facts =
            [
                ReportFacts.Count("Sessions", sessions.Count).Of("night"),
                .. figures.Where(f => f.Values.Count > 0).Take(ReportFacts.Max - 1)
                    .Select(f => new ReportFact($"Mean {f.Words[..1].ToLowerInvariant()}{f.Words[1..]}", ReportFacts.Number(f.Values.Average(), f.Format),
                        f.Unit.Length == 0 ? null : f.Unit, About: [f.Field])),
            ],
            Sentence = sentence ?? lines[0],
            SentenceAbout = about,
            OtherSentences = others,
            Omitted = omitted,
            Context = context,
        };
    }

    internal static List<IReadOnlyDictionary<string, object?>> Rows(IEnumerable<SleepSession> sessions, TimeZoneInfo zone, bool body) =>
    [
        .. sessions.Select(s => SeriesRows.Row(
            ("night", SeriesRows.DayOf(SeriesRows.Mills(s.EndTime), zone)),
            ("score", s.SleepScore is { } score ? (double)score : null),
            ("efficiency", SeriesRows.Round(s.Efficiency, 1)),
            ("latency", s.SleepLatencyMs is { } ms ? ScaledRounding.Round(ms / 60_000.0, 0) : null),
            ("restless", s.RestlessPeriods is { } r ? (double)r : null),
            ("avgHeartRate", body ? SeriesRows.Round(s.AvgHeartRate, 0) : null),
            ("minHeartRate", body ? SeriesRows.Round(s.MinHeartRate, 0) : null),
            ("hrv", body ? SeriesRows.Round(s.AvgHrv, 0) : null),
            ("spo2", body ? SeriesRows.Round(s.AvgSpo2, 1) : null),
            ("breathingRate", body ? SeriesRows.Round(s.AvgBreathRate, 1) : null))),
    ];
}

/// <summary>
/// The heart rate, HRV, blood oxygen and breathing rate a wearable sampled during sleep, as the mean of each slot.
/// Every figure is a heart reading or worked out of one, so it needs <c>heartrate.read</c> beside the dataset's
/// <c>sleep.read</c>. Read in the request's own context: <c>sleep_biometric_samples</c> is hidden from shares and
/// shown to a clamped member exactly while its session is (<see cref="ShareDataCategories.HiddenParentRecency"/>).
/// </summary>
internal sealed class SleepBiometricsDataset : IReportDataset
{
    private const int Budget = 1500;

    /// <summary>The most samples one read takes: a minute's samples over the longest range.</summary>
    private const int MaxSamples = 60_000;

    public string Name => SleepBiometricsParameters.Name;

    public string Label => "Sleep body readings";

    public string Description => "Heart rate, HRV, blood oxygen and breathing rate during sleep, over time.";

    public Type ParametersType => typeof(SleepBiometricsParameters);

    public string RequiredScope => Scope.SleepRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("time", "Time", ReportFieldKind.Time, null, "Start of the slot, Unix milliseconds."),
        new("heartRate", "Heart rate in sleep", ReportFieldKind.Number, "bpm", "The mean of the heart rate samples in the slot.", Scope.HeartRateRead, ChartColor.HeartRate, Aggregate: ReportAggregate.Mean),
        new("hrv", "HRV in sleep", ReportFieldKind.Number, "ms", "The mean of the heart rate variability samples in the slot.", Scope.HeartRateRead, ChartColor.Chart4, Aggregate: ReportAggregate.Mean),
        new("spo2", "Blood oxygen in sleep", ReportFieldKind.Percent, "%", "The mean of the blood oxygen (SpO2) samples in the slot.", Scope.HeartRateRead, ChartColor.Chart3, Aggregate: ReportAggregate.Mean),
        new("breathingRate", "Breathing rate in sleep", ReportFieldKind.Number, "breaths/min", "The mean of the breathing rate samples in the slot.", Scope.HeartRateRead, ChartColor.Chart5, Aggregate: ReportAggregate.Mean),
    ];

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Line, ReportMark.Points];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) =>
        mark is ReportMark.Line or ReportMark.Points ? new ReportLayerBindings { X = "time", Y = ["heartRate"] } : null;

    public TimeSpan DefaultRange => TimeSpan.FromDays(1);

    public TimeSpan MaxRange => TimeSpan.FromDays(31);

    public int PointBudget => Budget;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    internal sealed record Sample(long Mills, float? HeartRate, float? Hrv, float? Spo2, float? RespirationRate);

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var context = ReportFacts.Window(request.From, request.To, request.Zone);
        if (!request.HasScope(Scope.HeartRateRead))
            return SeriesRows.Empty("Not shown, because the token does not hold the heart rate scope.", $"{context}.",
                [.. Fields.Where(f => f.Scope is not null).Select(f => new ReportOmittedField(f.Name, Scope.HeartRateRead))]);

        var (start, end) = (request.From.UtcDateTime, request.To.UtcDateTime);
        await using var db = await request.Services.GetRequiredService<INocturneDbContextFactory>().CreateAsync(cancellationToken);
        var read = await db.SleepBiometricSamples.AsNoTracking()
            .Where(s => s.Timestamp >= start && s.Timestamp < end)
            .OrderBy(s => s.Timestamp).Take(MaxSamples)
            .Select(s => new { s.Timestamp, s.HeartRate, s.Hrv, s.Spo2, s.RespirationRate })
            .ToListAsync(cancellationToken);
        var samples = read.Select(s => new Sample(SeriesRows.Mills(s.Timestamp), s.HeartRate, s.Hrv, s.Spo2, s.RespirationRate)).ToList();
        if (samples.Count > 0)
            request.ReadLog.Record("sleep_biometric_samples", samples.Count);
        if (samples.Count == 0)
            return SeriesRows.Empty("No body readings during sleep in this period.", $"{context}. No samples.");

        var interval = GlucoseSeriesDataset.IntervalFor(request.From, request.To, 1, Budget);
        var rows = Rows(samples, request.From.ToUnixTimeMilliseconds(), interval);
        List<double> Of(Func<Sample, float?> value) => [.. samples.Select(value).Where(v => v is > 0).Select(v => (double)v!.Value)];
        var figures = new List<(string Field, string Words, IReadOnlyList<double> Values, string Unit, string Format)>
        {
            ("heartRate", "Heart rate in sleep", Of(s => s.HeartRate), "bpm", "0"),
            ("hrv", "HRV in sleep", Of(s => s.Hrv), "ms", "0"),
            ("spo2", "Blood oxygen in sleep", Of(s => s.Spo2), "%", "0.#"),
            ("breathingRate", "Breathing rate in sleep", Of(s => s.RespirationRate), "breaths/min", "0.#"),
        };
        var (sentence, about, others) = SeriesRows.Sentences(figures);
        var lines = new List<string> { $"Body readings during sleep from {context}: {ReportFacts.Plural(samples.Count, "sample")}." };
        lines.AddRange(new[] { sentence }.OfType<string>().Concat(others.Select(o => o.Text)));
        var facts = figures.Where(f => f.Values.Count > 0)
            .Select(f => new ReportFact($"Lowest {f.Words[..1].ToLowerInvariant()}{f.Words[1..]}", ReportFacts.Number(f.Values.Min(), f.Format), f.Unit, About: [f.Field]))
            .ToList();
        if (samples.Count == MaxSamples)
            lines.Add($"Only the first {MaxSamples} samples are read.");
        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = string.Join('\n', lines) + (interval > 1 ? $"\nEach row is {interval} minutes." : ""),
            Facts = facts,
            Sentence = sentence ?? lines[0],
            SentenceAbout = about,
            OtherSentences = others,
            Context = $"{context}. {ReportFacts.Plural(samples.Count, "sample")}.",
            Resolution = new ReportResolution(1, interval, samples.Count, rows.Count, interval > 1),
        };
    }

    /// <summary>The mean of each figure in each <paramref name="interval"/>-minute slot that holds a sample.</summary>
    internal static List<IReadOnlyDictionary<string, object?>> Rows(IReadOnlyList<Sample> samples, long from, int interval)
    {
        var width = interval * LocalSlotBinning.MinuteMs;
        static double? Mean(IEnumerable<float?> values, int digits)
        {
            var held = values.Where(v => v is > 0).Select(v => (double)v!.Value).ToList();
            return held.Count == 0 ? null : ScaledRounding.Round(held.Average(), digits);
        }
        return
        [
            .. samples.GroupBy(s => from + (s.Mills - from) / width * width).OrderBy(g => g.Key)
                .Select(g => SeriesRows.Row(
                    ("time", g.Key),
                    ("heartRate", Mean(g.Select(s => s.HeartRate), 0)),
                    ("hrv", Mean(g.Select(s => s.Hrv), 0)),
                    ("spo2", Mean(g.Select(s => s.Spo2), 1)),
                    ("breathingRate", Mean(g.Select(s => s.RespirationRate), 1)))),
        ];
    }
}

/// <summary>
/// Each lab HbA1c result on the day it was measured. Read in the request's own context: <c>lab_hba1c_results</c> is
/// hidden from shares and clamped for a member; its endpoints are glucose reads.
/// </summary>
internal sealed class LabHbA1cDataset : IReportDataset
{
    private const int Budget = 500;

    public string Name => LabHbA1cParameters.Name;

    public string Label => "Lab HbA1c";

    public string Description => "Each HbA1c result from a lab, on the day it was measured.";

    public Type ParametersType => typeof(LabHbA1cParameters);

    public string RequiredScope => Scope.GlucoseRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("day", "Day", ReportFieldKind.Day, null, "The local day it was measured: its midnight, Unix milliseconds."),
        new("hba1c", "Lab HbA1c", ReportFieldKind.Percent, "%", "The HbA1c result, in percent (NGSP).", Color: ChartColor.GlucoseInRange, Aggregate: ReportAggregate.Last),
    ];

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Points, ReportMark.Line];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) =>
        mark is ReportMark.Points or ReportMark.Line ? new ReportLayerBindings { X = "day", Y = ["hba1c"] } : null;

    public TimeSpan DefaultRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => Budget;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var zone = await SeriesRows.ZoneAsync(request, cancellationToken);
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.From, zone).DateTime);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.To, zone).DateTime);
        var (start, end) = (first.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), last.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        await using var db = await request.Services.GetRequiredService<INocturneDbContextFactory>().CreateAsync(cancellationToken);
        var results = await db.LabHbA1cResults.AsNoTracking()
            .Where(r => r.MeasuredAt >= start && r.MeasuredAt < end)
            .OrderBy(r => r.MeasuredAt).Take(Budget)
            .Select(r => new { r.MeasuredAt, r.ValuePercent })
            .ToListAsync(cancellationToken);
        if (results.Count > 0)
            request.ReadLog.Record("lab_hba1c_results", results.Count);
        var context = $"{ReportFacts.Window(request.From, request.To, request.Zone)}. {ReportFacts.Plural(results.Count, "lab result")}.";
        if (results.Count == 0)
            return SeriesRows.Empty("No lab HbA1c results in this period.", context);

        var rows = results.Select(r => SeriesRows.Row(
            ("day", Midnight(DateOnly.FromDateTime(r.MeasuredAt), zone)),
            ("hba1c", ScaledRounding.Round(r.ValuePercent, 1)))).ToList();
        var latest = results[^1];
        var sentence = $"Latest lab HbA1c: {ReportFacts.Number(latest.ValuePercent)}% on {DateOnly.FromDateTime(latest.MeasuredAt):yyyy-MM-dd}, "
            + $"of {ReportFacts.Plural(results.Count, "result")} in this period.";
        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = sentence + (results.Count > 1
                ? $"\nFrom {ReportFacts.Number(results.Min(r => r.ValuePercent))}% to {ReportFacts.Number(results.Max(r => r.ValuePercent))}%."
                : ""),
            Facts =
            [
                new ReportFact("Latest", ReportFacts.Number(latest.ValuePercent), "%", $"{DateOnly.FromDateTime(latest.MeasuredAt):yyyy-MM-dd}", ["hba1c"]),
                ReportFacts.Count("Results", results.Count).Of("hba1c"),
            ],
            Sentence = sentence,
            SentenceAbout = ["hba1c"],
            Context = context,
        };
    }

    internal static long Midnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUnixTimeMilliseconds();
    }
}

/// <summary>
/// Each body weight record: weight, body fat and lean mass. Read in the request's own context: <c>body_weights</c> is
/// hidden from shares and clamped for a member; its endpoints are therapy reads.
/// </summary>
internal sealed class BodyWeightDataset : IReportDataset
{
    private const int Budget = 1000;

    public string Name => BodyWeightParameters.Name;

    public string Label => "Body weight";

    public string Description => "Each body weight record: weight, body fat and lean mass.";

    public Type ParametersType => typeof(BodyWeightParameters);

    public string RequiredScope => Scope.TherapyRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("time", "Time", ReportFieldKind.Time, null, "When it was recorded, Unix milliseconds."),
        new("weight", "Weight", ReportFieldKind.Number, "kg", "Body weight.", Color: ChartColor.Chart4, Step: true, Aggregate: ReportAggregate.Last),
        new("bodyFat", "Body fat", ReportFieldKind.Percent, "%", "The percent of the body weight that is fat, where the scale measures it.", Color: ChartColor.Chart5, Step: true, Aggregate: ReportAggregate.Last),
        new("leanMass", "Lean mass", ReportFieldKind.Number, "kg", "The weight that is not fat, where the scale measures it.", Color: ChartColor.Chart3, Step: true, Aggregate: ReportAggregate.Last),
    ];

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Line, ReportMark.Points];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) =>
        mark is ReportMark.Line or ReportMark.Points ? new ReportLayerBindings { X = "time", Y = ["weight"] } : null;

    public TimeSpan DefaultRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => Budget;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var (from, to) = (request.From.ToUnixTimeMilliseconds(), request.To.ToUnixTimeMilliseconds());
        await using var db = await request.Services.GetRequiredService<INocturneDbContextFactory>().CreateAsync(cancellationToken);
        var records = await db.BodyWeights.AsNoTracking()
            .Where(w => w.Mills >= from && w.Mills < to)
            .OrderBy(w => w.Mills).Take(Budget)
            .Select(w => new { w.Mills, w.WeightKg, w.BodyFatPercent, w.LeanMassKg })
            .ToListAsync(cancellationToken);
        if (records.Count > 0)
            request.ReadLog.Record("body_weights", records.Count);
        var context = $"{ReportFacts.Window(request.From, request.To, request.Zone)}. {ReportFacts.Plural(records.Count, "record")}.";
        if (records.Count == 0)
            return SeriesRows.Empty("No body weight records in this period.", context);

        var rows = records.Select(w => SeriesRows.Row(
            ("time", w.Mills),
            ("weight", ScaledRounding.Round((double)w.WeightKg, 1)),
            ("bodyFat", w.BodyFatPercent is { } fat ? ScaledRounding.Round((double)fat, 1) : null),
            ("leanMass", w.LeanMassKg is { } lean ? ScaledRounding.Round((double)lean, 1) : null))).ToList();
        var weights = records.Select(w => (double)w.WeightKg).ToList();
        var change = weights[^1] - weights[0];
        var sentence = $"Weight: {ReportFacts.Number(weights[^1])} kg on "
            + $"{ReportFacts.Instant(DateTimeOffset.FromUnixTimeMilliseconds(records[^1].Mills), request.Zone)}, "
            + $"{(change >= 0 ? "+" : "")}{ReportFacts.Number(change)} kg in this period.";
        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = $"{sentence}\nFrom {ReportFacts.Number(weights.Min())} kg to {ReportFacts.Number(weights.Max())} kg over {ReportFacts.Plural(records.Count, "record")}.",
            Facts =
            [
                new ReportFact("Latest", ReportFacts.Number(weights[^1]), "kg", About: ["weight"]),
                new ReportFact("Change", $"{(change >= 0 ? "+" : "")}{ReportFacts.Number(change)}", "kg", "in this period", ["weight"]),
            ],
            Sentence = sentence,
            SentenceAbout = ["weight"],
            OtherSentences = SeriesRows.Sentences(
            [
                ("bodyFat", "Body fat", [.. records.Select(w => w.BodyFatPercent).OfType<decimal>().Select(v => (double)v)], "%", "0.#"),
                ("leanMass", "Lean mass", [.. records.Select(w => w.LeanMassKg).OfType<decimal>().Select(v => (double)v)], "kg", "0.#"),
            ]) is var (first, firstAbout, rest) && first is not null ? [new ReportSentence(first, firstAbout!), .. rest] : [],
            Context = context,
        };
    }
}

/// <summary>
/// What each sensor reading carries beside its glucose: the rate of change the sensor reported and its noise level,
/// from the run's shared readings (<see cref="ReportRunCache.GlucoseAsync"/>), the first reading of each slot.
/// </summary>
internal sealed class SensorDetailDataset : IReportDataset
{
    private const int Budget = 2000;

    public string Name => SensorDetailParameters.Name;

    public string Label => "Sensor detail";

    public string Description => "The rate of change and the noise each sensor reading carries.";

    public Type ParametersType => typeof(SensorDetailParameters);

    public string RequiredScope => Scope.GlucoseRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("time", "Time", ReportFieldKind.Time, null, "When the sensor read it, Unix milliseconds."),
        new("trendRate", "Glucose rate of change", ReportFieldKind.Number, ReportField.GlucoseUnit + "/min",
            "How fast glucose was changing, as the sensor reported it, per minute. Negative when it was falling.", Color: ChartColor.Chart4,
            Aggregate: ReportAggregate.Mean),
        new("noise", "Sensor noise", ReportFieldKind.Number, null, "The noise level the sensor reported, 0 (none) to 4 (heavy).",
            Color: ChartColor.MutedForeground, Aggregate: ReportAggregate.Max),
    ];

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Line, ReportMark.Points, ReportMark.Bars];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) =>
        mark is ReportMark.Line or ReportMark.Points or ReportMark.Bars ? new ReportLayerBindings { X = "time", Y = ["trendRate"] } : null;

    public TimeSpan DefaultRange => TimeSpan.FromDays(1);

    public TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => Budget;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var readings = (await request.Cache.GlucoseAsync(request, cancellationToken))
            .Where(r => r.TrendRate is not null || r.Noise is not null).OrderBy(r => r.Mills).ToList();
        var context = $"{ReportFacts.Window(request.From, request.To, request.Zone)}. {ReportFacts.Plural(readings.Count, "reading")} with a rate or noise.";
        if (readings.Count == 0)
            return SeriesRows.Empty("No sensor readings with a rate of change or noise in this period.", context);

        var interval = GlucoseSeriesDataset.IntervalFor(request.From, request.To, 1, Budget);
        var rows = DeviceStatusDataset.Thin(readings, r => r.Mills, request.From.ToUnixTimeMilliseconds(), interval)
            .Select(r => SeriesRows.Row(("time", r.Mills), ("trendRate", Rate(r.TrendRate, request.UseMmol)), ("noise", r.Noise is { } n ? (double)n : null)))
            .ToList();
        var rates = readings.Where(r => r.TrendRate is not null).Select(r => r.TrendRate!.Value).ToList();
        var noisy = readings.Count(r => r.Noise is >= 3);
        var unit = $"{ReportFacts.GlucoseUnit(request.UseMmol)}/min";
        var lines = new List<string> { $"Sensor detail from {ReportFacts.Window(request.From, request.To, request.Zone)}." };
        var facts = new List<ReportFact>();
        var sentences = new List<ReportSentence>();
        if (rates.Count > 0)
        {
            var fastestFall = Rate(rates.Min(), request.UseMmol)!.Value;
            var fastestRise = Rate(rates.Max(), request.UseMmol)!.Value;
            sentences.Add(new($"Rate of change: from {ReportFacts.Number(fastestFall, "0.##")} to {ReportFacts.Number(fastestRise, "0.##")} {unit}.", ["trendRate"]));
            facts.Add(new ReportFact("Fastest fall", ReportFacts.Number(fastestFall, "0.##"), unit, About: ["trendRate"]));
            facts.Add(new ReportFact("Fastest rise", ReportFacts.Number(fastestRise, "0.##"), unit, About: ["trendRate"]));
        }
        if (readings.Any(r => r.Noise is not null))
        {
            sentences.Add(new($"Readings with heavy noise (3 or 4): {noisy}.", ["noise"]));
            facts.Add(ReportFacts.Count("Heavy noise", noisy, "readings").Of("noise"));
        }
        lines.AddRange(sentences.Select(s => s.Text));
        return new ReportDatasetOutput
        {
            Rows = rows,
            Summary = string.Join('\n', lines) + (interval > 1 ? $"\nEach row is the first reading of {interval} minutes." : ""),
            Facts = facts,
            Sentence = sentences.Count > 0 ? sentences[0].Text : lines[0],
            SentenceAbout = sentences.Count > 0 ? sentences[0].About : null,
            OtherSentences = sentences.Skip(1).ToList(),
            Context = context,
            Resolution = new ReportResolution(1, interval, readings.Count, rows.Count, rows.Count < readings.Count),
        };
    }

    /// <summary>A rate in mg/dL a minute in the display unit, finer than a glucose value since it is a small number.</summary>
    internal static double? Rate(double? mgdlPerMinute, bool useMmol) => mgdlPerMinute is not { } v ? null
        : useMmol ? ScaledRounding.Round(v / GlucoseConstants.MgdlPerMmol, 2) : ScaledRounding.Round(v, 1);
}

/// <summary>
/// Each blood glucose reading a meter sent through the pump or uploader (<see cref="IMeterGlucoseRepository"/>), as
/// distinct from the BG checks entered by hand. Read through the request's own repository: <c>meter_glucose</c> is
/// shared under <c>glucose.read</c> and clamped like every time series.
/// </summary>
internal sealed class MeterGlucoseDataset : IReportDataset
{
    private const int Budget = 2000;

    public string Name => MeterGlucoseParameters.Name;

    public string Label => "Meter readings";

    public string Description => "Each blood glucose reading a meter sent through the pump or uploader.";

    public Type ParametersType => typeof(MeterGlucoseParameters);

    public string RequiredScope => Scope.GlucoseRead;

    public IReadOnlyList<ReportField> Fields { get; } =
    [
        new("time", "Time", ReportFieldKind.Time, null, "When the meter read it, Unix milliseconds."),
        new("meterGlucose", "Meter glucose", ReportFieldKind.Number, ReportField.GlucoseUnit, "The meter's reading.",
            Color: ChartColor.GlucoseInRange, Aggregate: ReportAggregate.Mean),
    ];

    public IReadOnlyList<ReportPanelKind> PanelKinds { get; } = [ReportPanelKind.Table];

    public IReadOnlyList<ReportMark> Marks { get; } = [ReportMark.Points, ReportMark.Line];

    public ReportLayerBindings? DefaultBindings(ReportMark mark) =>
        mark is ReportMark.Points or ReportMark.Line ? new ReportLayerBindings { X = "time", Y = ["meterGlucose"] } : null;

    public TimeSpan DefaultRange => TimeSpan.FromDays(14);

    public TimeSpan MaxRange => TimeSpan.FromDays(V4ReadLimits.MaxAnalyticsSpanDays);

    public int PointBudget => Budget;

    public IEnumerable<(string Field, string Message)> Validate(ReportDatasetParameters parameters) => [];

    public async Task<ReportDatasetOutput> RunAsync(ReportDatasetRequest request, CancellationToken cancellationToken)
    {
        var readings = (await request.Services.GetRequiredService<IMeterGlucoseRepository>().GetAsync(
                request.From.UtcDateTime, request.To.UtcDateTime, null, null, Budget + 1, 0, descending: true, cancellationToken))
            .Where(m => m.Mgdl > 0).OrderBy(m => m.Mills).ToList();
        var cut = readings.Count > Budget;
        if (cut)
            readings = readings[1..];
        if (readings.Count > 0)
            request.ReadLog.Record("meter_glucose", readings.Count);
        var context = $"{ReportFacts.Window(request.From, request.To, request.Zone)}. {ReportFacts.Plural(readings.Count, "meter reading")}.";
        if (readings.Count == 0)
            return SeriesRows.Empty("No meter readings in this period.", context);

        var useMmol = request.UseMmol;
        var values = readings.Select(m => m.Mgdl).ToList();
        string G(double mgdl) => GlucoseSummary.Format(mgdl, useMmol);
        var unit = ReportFacts.GlucoseUnit(useMmol);
        var sentence = $"Meter readings: {readings.Count}, mean {G(values.Average())} {unit}, from {G(values.Min())} to {G(values.Max())} {unit}.";
        string? cutNote = cut
            ? $"Only the latest {Budget} readings are shown, from {ReportFacts.Instant(DateTimeOffset.FromUnixTimeMilliseconds(readings[0].Mills), request.Zone)}."
            : null;
        return new ReportDatasetOutput
        {
            Rows = [.. readings.Select(m => SeriesRows.Row(("time", m.Mills), ("meterGlucose", Extensions.GlucoseUnits.Scale(m.Mgdl, useMmol))))],
            Summary = cutNote is null ? sentence : $"{sentence}\n{cutNote}",
            Facts =
            [
                ReportFacts.Count("Readings", readings.Count).Of("meterGlucose"),
                ReportFacts.Glucose("Mean", values.Average(), useMmol).Of("meterGlucose"),
            ],
            Sentence = sentence,
            SentenceAbout = ["meterGlucose"],
            CompleteFrom = cut ? readings[0].Mills : null,
            CutNote = cutNote,
            Context = context,
        };
    }
}
