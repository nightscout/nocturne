using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;

namespace Nocturne.API.Services.Analytics;

/// <summary>A Loop Analysis request resolved against the tenant clock and the caller.</summary>
/// <param name="Filter">Null when <see cref="Zone"/> is: without a zone the dates cannot be placed.</param>
internal sealed record LoopAnalysisRequest(
    LoopAnalysisFilter? Filter,
    TimeZoneInfo? Zone,
    TimeZoneUnavailableReason? Reason,
    LoopAnalysisResolvedRange? PrimaryRange,
    LoopAnalysisResolvedRange? ComparisonRange,
    LoopAnalysisMasks Masks,
    List<string> IgnoredFilters,
    List<LoopAnalysisUnavailableFilter> UnavailableFilters,
    bool HistoryClamped)
{
    /// <summary>Writes what every page reports about how its request was read into <paramref name="response"/>.</summary>
    public T Fill<T>(T response) where T : LoopAnalysisResponse
    {
        response.HistoryClamped = HistoryClamped;
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
    /// A history-clamped caller is analysed over its last <see cref="HistoryClamp.Window"/> whatever
    /// dates it asked for, with no comparison. Every resolved range ends after it starts and no later
    /// than now. The query must have no <see cref="Problem"/>.
    /// </summary>
    /// <param name="passed">The parameters the caller sent; only those are reported as ignored.</param>
    public async Task<LoopAnalysisRequest> ResolveAsync(
        LoopAnalysisQuery query,
        IReadOnlySet<string> granted,
        bool clamped,
        IReadOnlySet<string> passed,
        CancellationToken ct)
    {
        var masks = query.ToMasks();
        var ignored = LoopAnalysisReadScopeGuard.NarrowFilter(masks, granted);
        var unavailable = LoopAnalysisReadScopeGuard.UnavailableFilters(granted);
        if (clamped)
            ignored.AddRange(["startDate", "endDate", "compare", "compareStartDate", "compareEndDate"]);
        ignored = [.. ignored.Where(passed.Contains)];

        var (zone, reason) = await tenantClock.ResolveAsync(ct);
        if (zone is null)
            return new LoopAnalysisRequest(null, null, reason, null, null, masks, ignored, unavailable, clamped);

        var maxDays = V4ReadLimits.MaxAnalyticsSpanDays;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var primary = clamped
            ? LoopAnalysisRanges.Clamped(now, HistoryClamp.Window, zone)
            : LoopAnalysisRanges.Resolve(query.StartDate, query.EndDate, zone, maxDays, now);

        var comparison = clamped ? null : query.Compare switch
        {
            LoopAnalysisComparison.Previous => LoopAnalysisRanges.Previous(primary, zone),
            LoopAnalysisComparison.Custom => LoopAnalysisRanges.Resolve(
                query.CompareStartDate!.Value, query.CompareEndDate!.Value, zone, maxDays, now),
            _ => null,
        };

        var filter = new LoopAnalysisFilter
        {
            From = primary.From,
            To = primary.To,
            CompareFrom = comparison?.From,
            CompareTo = comparison?.To,
            Now = now,
            Masks = masks,
            ReadProfileNames = !LoopAnalysisReadScopeGuard.WithheldEnvelope(granted).Contains(LoopAnalysisSeries.ProfileNames),
            HistoryVisibleFrom = clamped ? primary.From : null,
        };
        return new LoopAnalysisRequest(filter, zone, null, primary, comparison, masks, ignored, unavailable, clamped);
    }

    private static bool OutOfRange(DateOnly date) => date < EarliestDate || date > LatestDate;
}
