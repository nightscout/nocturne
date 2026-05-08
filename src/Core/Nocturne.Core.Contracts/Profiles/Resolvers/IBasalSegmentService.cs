using Nocturne.Core.Models.Basal;

namespace Nocturne.Core.Contracts.Profiles.Resolvers;

/// <summary>
/// Streams the scheduled basal-rate timeline as piecewise-constant <see cref="BasalSegment"/>s
/// over a half-open window. The boundaries reflect schedule-entry transitions, profile switches,
/// schedule-version changes, and CCP adjustment changes.
/// </summary>
/// <remarks>
/// Replaces the per-instant point-sampling pattern that was forcing call-sites to issue
/// thousands of <see cref="IBasalRateResolver.GetBasalRateAsync"/> calls when integrating over
/// a window. <see cref="IBasalRateResolver"/> stays for genuine point queries (current rate
/// for UI, alert-engine evaluation, etc.).
/// </remarks>
public interface IBasalSegmentService
{
    /// <summary>
    /// Yield every basal segment overlapping <c>[fromMills, toMills)</c>, clipped to the window,
    /// in chronological order. Adjacent segments with identical effective rates are NOT merged.
    /// </summary>
    /// <param name="fromMills">Window start, Unix ms, inclusive.</param>
    /// <param name="toMills">Window end, Unix ms, exclusive.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<BasalSegment> GetSegmentsAsync(
        long fromMills,
        long toMills,
        CancellationToken ct = default);
}
