using System.Globalization;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.API.Reports.Datasets;

/// <summary>
/// What the Loop Analysis datasets share: a panel's range as the local days the Loop Analysis pages
/// read, resolved for the caller exactly as <c>LoopAnalysisController</c> resolves a request
/// (<see cref="LoopAnalysisRequestResolver"/>), and the sentences that say how it was read.
/// </summary>
internal static class LoopAnalysisDatasetRun
{
    /// <summary>The longest range any Loop Analysis dataset reads; the pages keep only the latest 90 days.</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(Controllers.V4.Base.V4ReadLimits.MaxAnalyticsSpanDays);

    /// <summary>
    /// The request for the local days the panel's range touches, with these masks and, when given, a
    /// comparison range. A history-clamped caller is read over the part of the range in its last
    /// 24 hours, to the instant, with no comparison.
    /// </summary>
    public static Task<LoopAnalysisRequest> ResolveAsync(
        ReportDatasetRequest request,
        LoopAnalysisDatasetParameters parameters,
        ReportDateRange? comparison,
        CancellationToken ct) =>
        ResolveAsync(request.Services, request.From, request.To, request.GrantedScopes, request.HistoryClamped, parameters, comparison, ct, request.Now);

    /// <summary>The request for the local days <paramref name="from"/> to <paramref name="to"/> touch, for a caller outside a report panel.</summary>
    /// <param name="asOf">The instant <paramref name="from"/> and <paramref name="to"/> were resolved against.</param>
    public static async Task<LoopAnalysisRequest> ResolveAsync(
        IServiceProvider services,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlySet<string> grantedScopes,
        bool historyClamped,
        LoopAnalysisDatasetParameters parameters,
        ReportDateRange? comparison,
        CancellationToken ct,
        DateTimeOffset? asOf = null)
    {
        var clock = services.GetRequiredService<TenantClockResolver>();
        var time = services.GetRequiredService<TimeProvider>();
        var zone = (await clock.ResolveAsync(ct)).Zone ?? TimeZoneInfo.Utc;

        var query = parameters.ToQuery(
            LocalSlotBinning.LocalDay(from.ToUnixTimeMilliseconds(), zone),
            LocalSlotBinning.LocalDay(to.ToUnixTimeMilliseconds() - 1, zone));
        var sent = new HashSet<string>(parameters.Sent(), StringComparer.OrdinalIgnoreCase);
        if (comparison is not null)
        {
            query.Compare = LoopAnalysisComparison.Custom;
            query.CompareStartDate = comparison.From;
            query.CompareEndDate = comparison.To;
            sent.UnionWith(["compare", "compareStartDate", "compareEndDate"]);
        }

        return await new LoopAnalysisRequestResolver(clock, time)
            .ResolveAsync(query, grantedScopes, historyClamped, sent, ct, (from.UtcDateTime, to.UtcDateTime), asOf?.UtcDateTime);
    }

    /// <summary>The page's response filled and narrowed for the caller, as the controller serves it.</summary>
    public static T Serve<T>(LoopAnalysisRequest resolved, T response, ReportDatasetRequest request) where T : LoopAnalysisResponse =>
        Serve(resolved, response, request.GrantedScopes);

    public static T Serve<T>(LoopAnalysisRequest resolved, T response, IReadOnlySet<string> grantedScopes) where T : LoopAnalysisResponse =>
        LoopAnalysisReadScopeGuard.Redact(resolved.Fill(response), grantedScopes);

    /// <summary>The range read, for the panel's window.</summary>
    public static ReportWindow? Window(LoopAnalysisResponse response) =>
        response.PrimaryRange is { } range
            ? new ReportWindow(new DateTimeOffset(range.From, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                new DateTimeOffset(range.To, TimeSpan.Zero).ToUnixTimeMilliseconds())
            : null;

    /// <summary>The sentence a panel opens with when no time zone placed its days, or null when one did.</summary>
    public static string? NoTimeZone(LoopAnalysisResponse response, string what) =>
        response.TimeZoneUnavailableReason is { } reason
            ? $"{what}: no data. The days cannot be placed without a time zone ({reason}). Set one in the profile."
            : null;

    /// <summary>How the request was read: the local days, the comparison, the history clamp and what the token could not use.</summary>
    public static IEnumerable<string> Context(LoopAnalysisResponse response, string what)
    {
        if (response.PrimaryRange is { } primary && !response.RangeOutsideHistory)
            yield return string.Create(CultureInfo.InvariantCulture,
                $"{what} over local days {primary.StartDate:yyyy-MM-dd} to {primary.EndDate:yyyy-MM-dd}{(primary.Shortened ? " (only the latest 90 days)." : ".")}");
        if (response.ComparisonRange is { } comparison)
            yield return string.Create(CultureInfo.InvariantCulture,
                $"Comparison range: local days {comparison.StartDate:yyyy-MM-dd} to {comparison.EndDate:yyyy-MM-dd}.");
        if (response.HistoryClamped)
            yield return HistoryClampNote(response);
        if (response.IgnoredFilters.Count > 0)
            yield return $"Not applied, because the token does not hold the scope they need: {string.Join(", ", response.IgnoredFilters)}.";
        var withheld = response.WithheldSeries.Where(s => s != LoopAnalysisSeries.ProfileNames).ToList();
        if (withheld.Count > 0)
            yield return $"Withheld, because the token does not hold the scope they need: {string.Join(", ", withheld)}.";
    }

    /// <summary>The panel's range line: its local days, and what was counted in them.</summary>
    public static string? ContextLine(LoopAnalysisResponse response, string counted)
    {
        if (response.TimeZoneUnavailableReason is not null || response.PrimaryRange is not { } primary || response.RangeOutsideHistory)
            return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"Local days {primary.StartDate:yyyy-MM-dd} to {primary.EndDate:yyyy-MM-dd}. {counted}.");
    }

    /// <summary>What the history clamp did to the range: the window served, or that the range is older than the window.</summary>
    internal static string HistoryClampNote(LoopAnalysisResponse response)
    {
        if (response.RangeOutsideHistory)
            return "No data: the range ends before the last 24 hours, and this token reads only the last 24 hours.";
        return !response.NarrowerThanHistoryWindow || response.PrimaryRange is not { } range
            ? "This token reads only the last 24 hours, so the analysis covers that window and has no comparison."
            : string.Create(CultureInfo.InvariantCulture,
                $"This token reads only the last 24 hours, so the analysis covers only the part of the range in that window, {range.From:yyyy-MM-dd HH:mm} to {range.To:yyyy-MM-dd HH:mm} UTC, and has no comparison.");
    }

    /// <summary>The scope that withholds a series from a caller; the first one when several do.</summary>
    public static string ScopeOf(LoopAnalysisSeries series) => series switch
    {
        LoopAnalysisSeries.Glucose or LoopAnalysisSeries.EventGlucose => Scope.GlucoseRead,
        LoopAnalysisSeries.ScheduledBasal or LoopAnalysisSeries.Target or LoopAnalysisSeries.DeliveredToScheduledRatio => Scope.TherapyRead,
        LoopAnalysisSeries.SettingsEvidence => Scope.TherapyRead,
        _ => Scope.TreatmentsRead,
    };

    /// <summary>The fields whose series the response withheld, for the panel's omitted list.</summary>
    public static IReadOnlyList<ReportOmittedField> Omitted(
        LoopAnalysisResponse response, IReadOnlyDictionary<string, LoopAnalysisSeries> fieldSeries) =>
        [.. fieldSeries
            .Where(f => response.WithheldSeries.Contains(f.Value))
            .Select(f => new ReportOmittedField(f.Key, ScopeOf(f.Value)))];

    /// <summary>A mg/dL figure in the display unit, or null.</summary>
    public static double? Glucose(double? mgdl, bool useMmol) => mgdl is { } v ? GlucoseUnits.Scale(v, useMmol) : null;

    /// <summary>A figure rounded for the rows.</summary>
    public static double? Round(double? value, int digits = 2) => value is { } v ? Math.Round(v, digits) : null;

    /// <summary>A local time of day, HH:mm, for minutes after midnight.</summary>
    public static string Clock(int minuteOfDay) =>
        string.Create(CultureInfo.InvariantCulture, $"{minuteOfDay / 60 % 24:00}:{minuteOfDay % 60:00}");

    public static string Number(double value, string format = "0.##") => value.ToString(format, CultureInfo.InvariantCulture);
}
