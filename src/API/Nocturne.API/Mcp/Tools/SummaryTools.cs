using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nocturne.API.Extensions;
using Nocturne.API.Mcp.Reports;
using Nocturne.API.Mcp.Reports.Datasets;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Mcp.Tools;

/// <summary>
/// The read-only text tools: each answers in words, under <see cref="MaxTextLength"/>, and never lists
/// the records it read. The two that read report datasets run a spec through <see cref="ReportRenderer"/>,
/// so they degrade, clamp and audit as <c>render_report</c> does, and return only its text.
/// </summary>
[McpServerToolType]
internal sealed class SummaryTools
{
    /// <summary>The most characters a text tool returns: 8 kB of ASCII.</summary>
    internal const int MaxTextLength = 8 * 1024;

    internal const int MaxDays = 90;

    internal const int MaxTreatmentHours = 14 * 24;

    [McpServerTool(Name = "get_glucose_statistics", Title = "Glucose statistics", ReadOnly = true, OpenWorld = false, Idempotent = true)]
    [Description(
        "Glucose statistics for the last few days, as text: readings, time in range, below and above range, mean, SD, CV, "
        + "GMI, the time in each glucose band (very low to very high), and with treatments.read insulin and carbs per day.")]
    [McpRequireScope(Scope.GlucoseRead)]
    public static async Task<CallToolResult> GetGlucoseStatistics(
        ReportSpecValidator validator,
        ReportRenderer renderer,
        [Description("How many days back from now, 1 to 90. Default 14.")] int days = 14,
        CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > MaxDays)
            throw new McpInvalidArgumentsException($"days must be between 1 and {MaxDays}.");

        var spec = PresetTools.Normalise(validator, new
        {
            version = ReportSpec.CurrentVersion,
            range = new { hours = days * 24 },
            panels = new object[]
            {
                new { id = "statistics", title = "Statistics", dataset = new { name = GlucoseStatisticsParameters.Name }, primitive = "stat_tiles" },
                new { id = "distribution", title = "Time in each glucose range", dataset = new { name = GlucoseDistributionParameters.Name }, primitive = "stat_tiles" },
            },
        }, await renderer.UsesMmolAsync(cancellationToken));
        return Text(ReportText(await renderer.RunAsync(spec, cancellationToken)));
    }

    [McpServerTool(Name = "get_recent_treatments", Title = "Recent treatments", ReadOnly = true, OpenWorld = false, Idempotent = true)]
    [Description(
        "The treatments of the last few hours, as text: for each kind the count, the total, the largest and the latest. "
        + "Kinds: bolus, carbs, temp_basal, note, override, basal_injection. To see the records themselves, use show_treatments "
        + "or a render_report treatments table.")]
    [McpRequireScope(Scope.TreatmentsRead)]
    public static async Task<CallToolResult> GetRecentTreatments(
        ReportSpecValidator validator,
        ReportRenderer renderer,
        [Description("How many hours back from now, 1 to 336. Default 24.")] int hours = 24,
        [Description("The kinds to count: bolus, carbs, temp_basal, note, override, basal_injection. Default every kind.")] string[]? types = null,
        CancellationToken cancellationToken = default)
    {
        if (hours is < 1 or > MaxTreatmentHours)
            throw new McpInvalidArgumentsException($"hours must be between 1 and {MaxTreatmentHours}.");

        var spec = new
        {
            version = ReportSpec.CurrentVersion,
            range = new { hours },
            panels = new object[]
            {
                types is null
                    ? new { id = "treatments", dataset = (object)new { name = TreatmentsParameters.Name }, primitive = "table" }
                    : new { id = "treatments", dataset = (object)new { name = TreatmentsParameters.Name, types }, primitive = "table" },
            },
        };
        var validation = validator.Validate(JsonSerializer.SerializeToElement(spec), await renderer.UsesMmolAsync(cancellationToken));
        if (!validation.IsValid)
            throw new McpInvalidArgumentsException(string.Join('\n', validation.Errors.Select(e =>
                $"{e.Path.Replace("panels[0].dataset.", "", StringComparison.Ordinal)}: {e.Message}")));

        return Text(ReportText(await renderer.RunAsync(validation.Spec!, cancellationToken)));
    }

    [McpServerTool(Name = "get_profile_summary", Title = "Profile summary", ReadOnly = true, OpenWorld = false, Idempotent = true)]
    [Description(
        "The therapy profiles in force over the last 24 hours, as text: each profile's name and share of the day, any "
        + "percentage or time shift, and its basal rates, carb ratios, insulin sensitivity and target ranges by time of day. "
        + "Needs therapy.read. Without it, the tool says so and shows nothing.")]
    public static async Task<CallToolResult> GetProfileSummary(
        IHttpContextAccessor httpContextAccessor,
        IActiveProfileSchedulesService schedules,
        ReportRenderer renderer,
        McpToolReadLog readLog,
        TimeProvider time,
        CancellationToken cancellationToken = default)
    {
        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("get_profile_summary ran outside an HTTP request.");
        if (!http.HasScope(Scope.TherapyRead))
            return Text("The profile is not shown: it needs the therapy.read scope, which this token does not hold.");

        var now = time.GetUtcNow();
        var profiles = await schedules.GetForRangeAsync(now.AddHours(-24).ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(), cancellationToken);
        readLog.Record("profiles", profiles.Count);
        return Text(ProfileText(profiles, await renderer.UsesMmolAsync(cancellationToken)));
    }

    [McpServerTool(Name = "get_loop_health", Title = "Loop health", ReadOnly = true, OpenWorld = false, Idempotent = true)]
    [Description(
        "How the automated insulin delivery loop ran over the last few days, as text: closed-loop, pump and CGM time, gaps "
        + "between loop cycles, zero delivery, time at the maximum basal rate and at the maximum IOB, automatic boluses, "
        + "suggestions not enacted and the sensitivity ratio. The figures of the Nocturne Loop health report. Figures the "
        + "token's scopes do not cover are left out and named.")]
    [McpRequireScope(Scope.DevicesRead)]
    public static async Task<CallToolResult> GetLoopHealth(
        IHttpContextAccessor httpContextAccessor,
        ILoopAnalysisService loopAnalysis,
        McpToolReadLog readLog,
        TimeProvider time,
        [Description("How many days back from now, 1 to 90. Default 14.")] int days = 14,
        CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > MaxDays)
            throw new McpInvalidArgumentsException($"days must be between 1 and {MaxDays}.");

        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("get_loop_health ran outside an HTTP request.");
        var scopes = http.GetGrantedScopes();
        var now = time.GetUtcNow();
        var resolved = await LoopAnalysisDatasetRun.ResolveAsync(
            http.RequestServices, now.AddDays(-days), now, scopes, http.IsCallerHistoryClamped(), new LoopHealthParameters(), null, cancellationToken);
        var report = resolved.Filter is { } filter && resolved.Zone is { } zone
            ? await loopAnalysis.GetLoopHealthAsync(filter, zone, cancellationToken)
            : new LoopHealthResponse();
        report = LoopAnalysisDatasetRun.Serve(resolved, report, scopes);
        report.LoopGapMinutes = (int)(LoopHealthCalculator.LoopGapMs / LocalSlotBinning.MinuteMs);
        RecordLoopHealthReads(report, readLog);
        return Text(LoopHealthText(report));
    }

    /// <summary>The Loop health masks: the page's defaults, illness and open loop left out.</summary>
    internal sealed record LoopHealthParameters : LoopAnalysisDatasetParameters
    {
        public override string DatasetName => "loop_health";
    }

    /// <summary>A text result, cut to <see cref="MaxTextLength"/> if it ever runs over.</summary>
    internal static CallToolResult Text(string text) => new()
    {
        Content = [new TextContentBlock { Text = Cap(text) }],
    };

    internal static string Cap(string text)
    {
        const string cut = "\n(Cut to 8 kB.)";
        return text.Length <= MaxTextLength ? text : text[..(MaxTextLength - cut.Length)] + cut;
    }

    private static string ReportText(ReportResult report)
    {
        var text = new StringBuilder(report.Summary);
        foreach (var warning in report.Warnings)
            text.Append('\n').Append("Warning: ").Append(warning);
        return text.ToString();
    }

    /// <summary>At most three profiles, each schedule's entries in order: a schedule has a few dozen at most.</summary>
    internal static string ProfileText(IReadOnlyList<ActiveProfileSchedules> profiles, bool useMmol)
    {
        if (profiles.Count == 0)
            return "No profile was in force over the last 24 hours.";

        static string N(double value, string format = "0.##") => value.ToString(format, CultureInfo.InvariantCulture);
        var unit = useMmol ? "mmol/L" : "mg/dL";
        string G(double mgdl) => GlucoseSummary.Format(mgdl, useMmol);
        string Entries(IEnumerable<ScheduleEntry> entries, Func<double, string> value) =>
            string.Join(", ", entries.Take(48).Select(e => $"{e.Time} {value(e.Value)}"));

        var text = new StringBuilder();
        foreach (var profile in profiles.Take(3))
        {
            text.Append(CultureInfo.InvariantCulture, $"Profile '{profile.ProfileName}': in force for {N(profile.Share * 100, "0")}% of the last 24 hours");
            if (profile.Percentage is { } percentage)
                text.Append(CultureInfo.InvariantCulture, $", at {N(percentage, "0")}%");
            if (profile.TimeshiftHours is { } shift && shift != 0)
                text.Append(CultureInfo.InvariantCulture, $", shifted {N(shift)} hours");
            text.Append(".\n");
            if (profile.Percentage is not null || profile.TimeshiftHours is not null)
                text.Append(profile.AdjustmentApplied
                    ? "The values below include the percentage and shift.\n"
                    : "The values below are the schedule's own, without the percentage and shift.\n");
            if (profile.Basal.Count > 0)
                text.Append("Basal (U/h): ").Append(Entries(profile.Basal, v => N(v, "0.###"))).Append(".\n");
            if (profile.CarbRatio.Count > 0)
                text.Append("Carb ratio (g/U): ").Append(Entries(profile.CarbRatio, v => N(v, "0.#"))).Append(".\n");
            if (profile.Sensitivity.Count > 0)
                text.Append(CultureInfo.InvariantCulture, $"Insulin sensitivity ({unit}/U): ").Append(Entries(profile.Sensitivity, G)).Append(".\n");
            if (profile.Targets.Count > 0)
                text.Append(CultureInfo.InvariantCulture, $"Target range ({unit}): ")
                    .Append(string.Join(", ", profile.Targets.Take(48).Select(t => $"{t.Time} {G(t.Low)}-{G(t.High)}"))).Append(".\n");
        }
        if (profiles.Count > 3)
            text.Append(CultureInfo.InvariantCulture, $"{profiles.Count - 3} more profiles were in force for a shorter time.\n");
        return text.ToString().TrimEnd();
    }

    /// <summary>The categories the served figures read; the figures are aggregates, so no count.</summary>
    internal static void RecordLoopHealthReads(LoopHealthResponse report, McpToolReadLog readLog)
    {
        if (report.Primary.Days.Count == 0)
            return;
        bool Served(params LoopAnalysisSeries[] series) => series.Any(s => !report.WithheldSeries.Contains(s));
        if (Served(LoopAnalysisSeries.CgmActivity))
            readLog.Record("glucose", 0);
        if (Served(LoopAnalysisSeries.LoopActivity, LoopAnalysisSeries.UnrecordedDelivery, LoopAnalysisSeries.ZeroDelivery,
                LoopAnalysisSeries.AutomaticBoluses, LoopAnalysisSeries.MaxBasal))
            readLog.Record("treatments", 0);
        if (Served(LoopAnalysisSeries.LoopActivity, LoopAnalysisSeries.LoopGaps, LoopAnalysisSeries.AtMaxIob,
                LoopAnalysisSeries.SuggestedNotEnacted, LoopAnalysisSeries.SensitivityRatio))
            readLog.Record("device_status", 0);
        if (Served(LoopAnalysisSeries.MaxBasal, LoopAnalysisSeries.LoopLimits, LoopAnalysisSeries.OrefMaxIob))
            readLog.Record("profiles", 0);
    }

    internal static string LoopHealthText(LoopHealthResponse report)
    {
        const string what = "Loop health";
        if (LoopAnalysisDatasetRun.NoTimeZone(report, what) is { } noZone)
            return noZone;

        static string P(double? percent) => percent is { } p ? p.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "no data";
        static string N(double value, string format = "0.#") => value.ToString(format, CultureInfo.InvariantCulture);
        var lines = new List<string>(LoopAnalysisDatasetRun.Context(report, what));
        var range = report.Primary;
        lines.Add($"Days analyzed: {range.Days.Count}.");
        if (range.Days.Count == 0)
            return string.Join('\n', lines);

        var activity = range.Activity;
        if (activity.AidActivePercent is not null || activity.PumpUsePercent is not null)
            lines.Add($"Closed loop active: {P(activity.AidActivePercent)} of the time. Pump in use: {P(activity.PumpUsePercent)}.");
        if (activity.CgmActivePercent is not null)
            lines.Add($"CGM active: {P(activity.CgmActivePercent)} of the time.");
        if (range.Cadence.LoopGaps is { } gaps)
            lines.Add($"Gaps between loop cycles longer than {report.LoopGapMinutes} minutes: {gaps.Count}, {N(gaps.TotalMinutes, "0")} minutes in all"
                + (gaps.LongestMinutes is { } longest ? $", the longest {N(longest, "0")} minutes." : "."));
        if (range.Cadence.UnrecordedDelivery is { } unrecorded)
            lines.Add($"Gaps in the pump's delivery records: {unrecorded.Count}, {N(unrecorded.TotalMinutes, "0")} minutes in all.");
        if (range.ZeroDelivery is { } zero
            && new[] { zero.AlgorithmZeroTemp.Percent, zero.ManualZeroTemp.Percent, zero.Suspended.Percent }.Any(p => p is not null))
            lines.Add($"Zero delivery: algorithm zero temp {P(zero.AlgorithmZeroTemp.Percent)}, manual zero temp {P(zero.ManualZeroTemp.Percent)}, "
                + $"suspended {P(zero.Suspended.Percent)} of the time.");
        if (range.MaxBasal is { AtCap.Percent: not null } maxBasal)
            lines.Add($"At the maximum basal rate: {P(maxBasal.AtCap.Percent)} of the time.");
        if (range.AtMaxIob is { Percent: not null } atMaxIob)
            lines.Add($"At the maximum IOB: {P(atMaxIob.Percent)} of the time.");
        if (range.AutomaticBoluses is { } automatic)
        {
            var perDay = automatic.PerDay.Overall;
            var size = automatic.Size.Overall;
            if (perDay is not null || size is not null)
                lines.Add("Automatic boluses: "
                    + (perDay is not null ? $"median {N(perDay.P50)} per day" : "")
                    + (perDay is not null && size is not null ? ", " : "")
                    + (size is not null ? $"median size {N(size.P50, "0.##")} U" : "") + ".");
        }
        if (range.SuggestedNotEnacted is { Percent: not null } notEnacted)
            lines.Add($"Suggestions not enacted: {P(notEnacted.Percent)}.");
        if (range.SensitivityRatio?.Overall is { } ratio)
            lines.Add($"Sensitivity ratio: median {N(ratio.P50, "0.##")} (middle half {N(ratio.P25, "0.##")} to {N(ratio.P75, "0.##")}).");
        if (range.Limits.LoopMaximumBasalRate is { } maxRate)
            lines.Add($"Maximum basal rate setting: {N(maxRate, "0.##")} U/h.");
        if (range.Limits.OrefMaxIob is { } maxIob)
            lines.Add($"Maximum IOB setting: {N(maxIob, "0.##")} U.");

        var unknown = new List<string>();
        void Unknown(string figure, LoopHealthUnknownReason? reason)
        {
            if (reason is { } r)
                unknown.Add($"{figure} ({r})");
        }
        Unknown("loop gaps", range.Unknown.LoopGaps);
        Unknown("delivery records", range.Unknown.UnrecordedDelivery);
        Unknown("time at maximum basal", range.Unknown.MaxBasal);
        Unknown("time at maximum IOB", range.Unknown.AtMaxIob);
        Unknown("suggestions not enacted", range.Unknown.SuggestedNotEnacted);
        if (unknown.Count > 0)
            lines.Add($"Not known from the data: {string.Join(", ", unknown)}.");
        return string.Join('\n', lines);
    }
}
