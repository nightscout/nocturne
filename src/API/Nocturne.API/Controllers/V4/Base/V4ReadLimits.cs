using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Clinical.LoopAnalysis;

namespace Nocturne.API.Controllers.V4.Base;

/// <summary>
/// Server-side ceilings on how much a V4 read may ask for.
/// </summary>
/// <remarks>
/// <para>
/// The v1 equivalents live in <c>LegacyReadLimits</c> and are deliberately not shared: those
/// ceilings are shaped by Nightscout compatibility, and either set may move without the other.
/// </para>
/// <para>
/// Over-large values are clamped rather than rejected, matching the existing V4 paging behaviour
/// in <c>ProfileController.GetProfileRecords</c>. An over-long date range is rejected instead, by
/// <see cref="RejectDateSpan(ControllerBase, DateTime?, DateTime?, int)"/>: silently narrowing a
/// range would answer a different question than the caller asked. Loop Analysis is the exception:
/// it trims to the most recent <see cref="MaxAnalyticsSpanDays"/> local days and says so with an
/// explicit <c>shortened</c> flag on the range it returns.
/// </para>
/// </remarks>
public static class V4ReadLimits
{
    /// <summary>
    /// Maximum records a V4 list read may return.
    /// </summary>
    /// <remarks>
    /// An order of magnitude above the largest first-party caller: the report and chart pages ask
    /// for 10,000 per list route, and nothing in the SDKs or connectors asks for more.
    /// </remarks>
    public const int MaxPageSize = 100_000;

    /// <summary>
    /// Maximum records reachable across a V4 read that merges several sources in memory before it
    /// paginates, counting both the page and everything skipped to reach it.
    /// </summary>
    /// <remarks>
    /// Bounds the window rather than the page because such a read over-fetches
    /// <c>limit + offset</c> records from every source it merges, so clamping <c>limit</c> alone
    /// would leave the cost proportional to <c>offset</c>. Deeper pages come back empty.
    /// </remarks>
    public const int MaxMergedPageWindow = 10_000;

    /// <summary>
    /// Maximum records one page of a page-numbered V4 read may return.
    /// </summary>
    /// <remarks>
    /// Three orders of magnitude below <see cref="MaxPageSize"/>, because a route addressing a page
    /// by ordinal exists to back a paged table rather than a bulk export: the alert-history pages
    /// ask for 25 and 50 rows, and pairing a larger page with a page number would also let a caller
    /// reach much further into the table for the same clamp.
    /// </remarks>
    public const int MaxOrdinalPageSize = 100;

    /// <summary>
    /// Maximum span, in days, between the <c>from</c> and <c>to</c> bounds of a V4 range read.
    /// </summary>
    /// <remarks>
    /// The widest range the report pages offer is 90 days, but their date picker also takes an
    /// arbitrary custom range, so the ceiling sits far enough above the presets to leave the picker
    /// usable. A range with only one bound set is not spanned and is left alone.
    /// </remarks>
    public const int MaxDateSpanDays = 366;

    /// <summary>
    /// Maximum local calendar days a CGM comparison or sleep trends window may cover.
    /// </summary>
    /// <remarks>
    /// Counted in the tenant's zone, from the day of <c>from</c> through the day of <c>to</c>
    /// inclusive, by <see cref="RejectAnalyticsSpanAsync"/>. A range of exactly this many days ends
    /// on the last millisecond of its final day, which is what the report pages send; ending on the
    /// midnight after it counts one day more. Loop Analysis trims to this many days instead of
    /// refusing, in <c>LoopAnalysisRanges</c>.
    /// </remarks>
    public const int MaxAnalyticsSpanDays = 90;

    private const int ZoneLookupMarginDays = 2;

    /// <summary>
    /// Maximum span, in days, of an actogram window.
    /// </summary>
    /// <remarks>
    /// The actogram report pads the picker range by 14 days each side for its double plot, so a
    /// picker range at <see cref="MaxDateSpanDays"/> arrives 28 days wider.
    /// </remarks>
    public const int MaxActogramSpanDays = MaxDateSpanDays + 28;

    /// <summary>Clamp a caller-supplied page size to <see cref="MaxPageSize"/>.</summary>
    public static int ClampLimit(int limit) => Math.Clamp(limit, 0, MaxPageSize);

    /// <summary>Normalize a caller-supplied offset to be non-negative.</summary>
    public static int ClampOffset(int offset) => Math.Max(0, offset);

    /// <summary>
    /// The records a merged read may return for a page starting at <paramref name="offset"/>, so
    /// that <paramref name="offset"/> cannot walk past <see cref="MaxMergedPageWindow"/>.
    /// </summary>
    public static int ClampMergedPage(int limit, int offset) =>
        Math.Clamp(limit, 0, MaxMergedPageWindow - Math.Min(ClampOffset(offset), MaxMergedPageWindow));

    /// <summary>
    /// Clamp a caller-supplied page size on a page-numbered read to <see cref="MaxOrdinalPageSize"/>.
    /// </summary>
    /// <remarks>
    /// The floor is one rather than zero: a page-numbered read reports a page count derived from the
    /// page size, so a zero page size has no meaning on this shape.
    /// </remarks>
    public static int ClampPageSize(int pageSize) => Math.Clamp(pageSize, 1, MaxOrdinalPageSize);

    /// <summary>
    /// Clamp a caller-supplied 1-based page number so the offset it implies stays within the first
    /// <see cref="MaxPageSize"/> records.
    /// </summary>
    /// <remarks>
    /// A page number is multiplied by the page size to reach an offset, so leaving it unbounded
    /// leaves the skip unbounded — and past <see cref="int"/> it wraps negative. Pass
    /// <paramref name="pageSize"/> already through <see cref="ClampPageSize"/>.
    /// </remarks>
    public static int ClampPageNumber(int page, int pageSize) =>
        Math.Clamp(page, 1, MaxPageSize / Math.Clamp(pageSize, 1, MaxPageSize));

    /// <summary>
    /// Rejects a range read whose bounds are further apart than <paramref name="maxDays"/>.
    /// </summary>
    /// <returns>The error response to return, or <c>null</c> when the range is usable.</returns>
    public static ObjectResult? RejectDateSpan(
        this ControllerBase controller, DateTime? from, DateTime? to, int maxDays = MaxDateSpanDays) =>
        from is { } start && to is { } end && (end - start).TotalDays > maxDays
            ? DateSpanProblem(controller, maxDays)
            : null;

    /// <inheritdoc cref="RejectDateSpan(ControllerBase, DateTime?, DateTime?, int)"/>
    /// <remarks>
    /// The difference is taken in <see cref="double"/> because two caller-supplied
    /// <see cref="long"/> bounds can be far enough apart to overflow a <see cref="long"/> subtraction.
    /// </remarks>
    public static ObjectResult? RejectDateSpan(
        this ControllerBase controller, long fromMills, long toMills, int maxDays = MaxDateSpanDays) =>
        (double)toMills - fromMills > (double)maxDays * TimeSpan.MillisecondsPerDay
            ? DateSpanProblem(controller, maxDays)
            : null;

    /// <summary>
    /// Rejects a CGM comparison or sleep trends range that covers more than
    /// <see cref="MaxAnalyticsSpanDays"/> days in the tenant's zone.
    /// </summary>
    /// <remarks>
    /// The zone is looked up only when the span in UTC is within <see cref="ZoneLookupMarginDays"/>
    /// of the cap, because outside that band no zone offset can change the answer. A tenant with no
    /// zone, and a share, which is never given one, count UTC days: the CGM comparison client cuts
    /// its days in UTC when the patient has no zone.
    /// </remarks>
    /// <returns>The error response to return, or <c>null</c> when the range is usable.</returns>
    public static async Task<ObjectResult?> RejectAnalyticsSpanAsync(
        this ControllerBase controller, DateTime? from, DateTime? to, TenantClockResolver tenantClock, CancellationToken ct)
    {
        if (from is not { } start || to is not { } end)
            return null;

        var spanDays = (end - start).TotalDays;
        if (spanDays < MaxAnalyticsSpanDays - ZoneLookupMarginDays)
            return null;
        if (spanDays > MaxAnalyticsSpanDays + ZoneLookupMarginDays)
            return DateSpanProblem(controller, MaxAnalyticsSpanDays);

        var (zone, _) = await tenantClock.ResolveAsync(ct);
        return controller.RejectLocalDaySpan(start, end, zone ?? TimeZoneInfo.Utc, MaxAnalyticsSpanDays);
    }

    /// <summary>
    /// Rejects a range read that covers more than <paramref name="maxDays"/> calendar days in
    /// <paramref name="zone"/>, counting the day of <paramref name="from"/> through the day of
    /// <paramref name="to"/> inclusive.
    /// </summary>
    /// <remarks>
    /// Bounds are read as UTC instants, which is what a query-bound <see cref="DateTime"/> with no
    /// offset is taken to be elsewhere in V4.
    /// </remarks>
    /// <returns>The error response to return, or <c>null</c> when the range is usable.</returns>
    public static ObjectResult? RejectLocalDaySpan(
        this ControllerBase controller, DateTime? from, DateTime? to, TimeZoneInfo zone, int maxDays)
    {
        if (from is not { } start || to is not { } end)
            return null;

        var (firstDay, lastDay) = LocalSlotBinning.LocalDaySpan(start, end, zone);
        return lastDay.DayNumber - firstDay.DayNumber + 1 > maxDays
            ? DateSpanProblem(controller, maxDays)
            : null;
    }

    private static ObjectResult DateSpanProblem(ControllerBase controller, int maxDays) =>
        controller.Problem(detail: $"Date range must not exceed {maxDays} days.", statusCode: 400, title: "Bad Request");
}
