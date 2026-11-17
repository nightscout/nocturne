namespace Nocturne.Core.Clinical.LoopAnalysis;

/// <summary>
/// Where a history-clamped caller's hidden rows stop mattering: what a calculation reads in a look
/// back before an instant is hidden while the look back crosses the clamp's visible edge, so it
/// reads as absent, not as none.
/// </summary>
public static class VisibleHistory
{
    /// <summary>
    /// The first instant whose look back of <paramref name="lookbackMs"/> lies wholly after
    /// <paramref name="visibleFrom"/>; <see cref="long.MinValue"/> where all history is visible.
    /// </summary>
    /// <param name="visibleFrom">See <see cref="Nocturne.Core.Models.LoopAnalysis.LoopAnalysisFilter.HistoryVisibleFrom"/>.</param>
    public static long ReadableFrom(long? visibleFrom, long lookbackMs) =>
        visibleFrom is { } edge ? edge + lookbackMs : long.MinValue;
}
