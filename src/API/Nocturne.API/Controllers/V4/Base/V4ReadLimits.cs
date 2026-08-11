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
/// in <c>ProfileController.GetProfileRecords</c>. An over-long date range is rejected instead,
/// matching <c>SleepReportController.GetTrends</c>: silently narrowing a range would answer a
/// different question than the caller asked.
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
    /// Maximum span, in days, between the <c>from</c> and <c>to</c> bounds of a V4 range read.
    /// </summary>
    /// <remarks>
    /// The widest range the report pages offer is 90 days, but their date picker also takes an
    /// arbitrary custom range, so the ceiling sits far enough above the presets to leave the picker
    /// usable. A range with only one bound set is not spanned and is left alone.
    /// </remarks>
    public const int MaxDateSpanDays = 366;

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
    /// Whether a range read's bounds are further apart than <see cref="MaxDateSpanDays"/>.
    /// </summary>
    public static bool ExceedsMaxDateSpan(DateTime? from, DateTime? to) =>
        from is { } start && to is { } end && (end - start).TotalDays > MaxDateSpanDays;
}
