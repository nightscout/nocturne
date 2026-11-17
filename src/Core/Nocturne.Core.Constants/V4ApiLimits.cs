namespace Nocturne.Core.Constants;

/// <summary>Limits of the V4 API that its own clients, such as a remote Nocturne connector, must respect.</summary>
public static class V4ApiLimits
{
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
    /// Maximum records reachable across a V4 read that merges several sources in memory before it
    /// paginates, counting both the page and everything skipped to reach it.
    /// </summary>
    /// <remarks>
    /// Bounds the window rather than the page because such a read over-fetches
    /// <c>limit + offset</c> records from every source it merges, so clamping <c>limit</c> alone
    /// would leave the cost proportional to <c>offset</c>. Deeper pages come back empty.
    /// </remarks>
    public const int MaxMergedPageWindow = 10_000;
}
