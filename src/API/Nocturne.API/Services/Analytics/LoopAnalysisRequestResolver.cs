using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.API.Services.Analytics;

/// <summary>A Loop Analysis request resolved against the tenant clock and the caller.</summary>
/// <param name="Filter">
/// Null when there is nothing to analyse: when <see cref="Zone"/> is null, since without a zone the
/// dates cannot be placed, and when <see cref="RangeOutsideHistory"/>.
/// </param>
/// <param name="RangeOutsideHistory">See <see cref="LoopAnalysisResponse.RangeOutsideHistory"/>.</param>
internal sealed record LoopAnalysisRequest(
    LoopAnalysisFilter? Filter,
    TimeZoneInfo? Zone,
    TimeZoneUnavailableReason? Reason,
    LoopAnalysisResolvedRange? PrimaryRange,
    LoopAnalysisResolvedRange? ComparisonRange,
    LoopAnalysisMasks Masks,
    List<string> IgnoredFilters,
    List<LoopAnalysisUnavailableFilter> UnavailableFilters,
    bool HistoryClamped,
    bool RangeOutsideHistory = false)
{
    /// <summary>Writes what every page reports about how its request was read into <paramref name="response"/>.</summary>
    public T Fill<T>(T response) where T : LoopAnalysisResponse
    {
        response.HistoryClamped = HistoryClamped;
        response.RangeOutsideHistory = RangeOutsideHistory;
        response.TimeZoneUnavailableReason = Reason;
        response.PrimaryRange = PrimaryRange;
        response.ComparisonRange = ComparisonRange;
        response.AppliedFilters = Masks;
        response.IgnoredFilters = IgnoredFilters;
        response.UnavailableFilters = UnavailableFilters;
        return response;
    }
}

/// <summary>
/// Turns a <see cref="LoopAnalysisQuery"/> into the <see cref="LoopAnalysisFilter"/> the service
/// computes from, for the Loop Analysis endpoints and the report datasets that read the same pages.
/// </summary>
internal sealed class LoopAnalysisRequestResolver(TenantClockResolver tenantClock, TimeProvider timeProvider)
{
    private static readonly DateOnly EarliestDate = new(1970, 1, 1);
    private static readonly DateOnly LatestDate = new(2999, 12, 31);

    /// <summary>Why the query's dates cannot be resolved, or null when they can.</summary>
    public static string? Problem(LoopAnalysisQuery query)
    {
        if (OutOfRange(query.StartDate) || OutOfRange(query.EndDate))
            return "startDate and endDate must be dates between 1970 and 2999.";
        if (query.EndDate < query.StartDate)
            return "endDate must not be before startDate.";

        var custom = query.Compare == LoopAnalysisComparison.Custom;
        if (custom != (query.CompareStartDate.HasValue && query.CompareEndDate.HasValue)
            || (!custom && (query.CompareStartDate.HasValue || query.CompareEndDate.HasValue)))
            return "compareStartDate and compareEndDate are given together, and only with compare=custom.";
        if (query is { CompareStartDate: { } compareStart, CompareEndDate: { } compareEnd })
        {
            if (OutOfRange(compareStart) || OutOfRange(compareEnd))
                return "compareStartDate and compareEndDate must be dates between 1970 and 2999.";
            if (compareEnd < compareStart)
                return "compareEndDate must not be before compareStartDate.";
        }
        return null;
    }

    /// <summary>
    /// Drops the masks the caller may not use (<see cref="LoopAnalysisReadScopeGuard.NarrowFilter"/>)
    /// and resolves the local dates to instants in the tenant zone (<see cref="LoopAnalysisRanges"/>).
    /// A history-clamped caller is analysed over the part of that range inside its last
    /// <see cref="HistoryClamp.Window"/>, with no comparison; a range that ends before the window is
    /// empty and flagged <see cref="LoopAnalysisRequest.RangeOutsideHistory"/>, with no filter. Every
    /// resolved range ends no later than now. The query must have no <see cref="Problem"/>.
    /// </summary>
    /// <param name="passed">The parameters the caller sent; only those are reported as ignored.</param>
    /// <param name="within">
    /// The instants a caller that asks in instants rather than days asked for (a report panel). A
    /// clamped caller's range is cut to them as well, since its window is not whole days anyway; an
    /// unclamped caller is read over the whole local days they touch, as on the pages.
    /// </param>
    public async Task<LoopAnalysisRequest> ResolveAsync(
        LoopAnalysisQuery query,
        IReadOnlySet<string> granted,
        bool clamped,
        IReadOnlySet<string> passed,
        CancellationToken ct,
        (DateTime From, DateTime To)? within = null)
    {
        var masks = query.ToMasks();
        var ignored = LoopAnalysisReadScopeGuard.NarrowFilter(masks, granted);
        var unavailable = LoopAnalysisReadScopeGuard.UnavailableFilters(granted);
        string[] comparisonParameters = ["compare", "compareStartDate", "compareEndDate"];

        var (zone, reason) = await tenantClock.ResolveAsync(ct);
        if (zone is null)
        {
            if (clamped)
                ignored.AddRange(["startDate", "endDate", .. comparisonParameters]);
            ignored = [.. ignored.Where(passed.Contains)];
            return new LoopAnalysisRequest(null, null, reason, null, null, masks, ignored, unavailable, clamped);
        }

        var maxDays = V4ReadLimits.MaxAnalyticsSpanDays;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var primary = LoopAnalysisRanges.Resolve(query.StartDate, query.EndDate, zone, maxDays, now);
        DateTime? visibleFrom = null;
        var outside = false;
        if (clamped)
        {
            var windowStart = now - HistoryClamp.Window;
            visibleFrom = windowStart;
            if (primary.From < windowStart)
                ignored.Add("startDate");
            if (primary.To <= windowStart)
                ignored.Add("endDate");
            ignored.AddRange(comparisonParameters);
            var (from, to) = within is { } asked
                ? (asked.From > windowStart ? asked.From : windowStart, asked.To < now ? asked.To : now)
                : (windowStart, now);
            primary = LoopAnalysisRanges.Intersect(primary, from, to, zone);
            outside = primary.To <= primary.From;
        }
        ignored = [.. ignored.Where(passed.Contains)];

        var comparison = clamped ? null : query.Compare switch
        {
            LoopAnalysisComparison.Previous => LoopAnalysisRanges.Previous(primary, zone),
            LoopAnalysisComparison.Custom => LoopAnalysisRanges.Resolve(
                query.CompareStartDate!.Value, query.CompareEndDate!.Value, zone, maxDays, now),
            _ => null,
        };

        var filter = outside ? null : new LoopAnalysisFilter
        {
            From = primary.From,
            To = primary.To,
            CompareFrom = comparison?.From,
            CompareTo = comparison?.To,
            Now = now,
            Masks = masks,
            ReadProfileNames = !LoopAnalysisReadScopeGuard.WithheldEnvelope(granted).Contains(LoopAnalysisSeries.ProfileNames),
            HistoryVisibleFrom = visibleFrom,
        };
        return new LoopAnalysisRequest(filter, zone, null, primary, comparison, masks, ignored, unavailable, clamped, outside);
    }

    private static bool OutOfRange(DateOnly date) => date < EarliestDate || date > LatestDate;
}
