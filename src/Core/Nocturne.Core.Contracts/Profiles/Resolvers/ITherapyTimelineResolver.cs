using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Profiles.Resolvers;

/// <summary>
/// Builds a <see cref="TherapyTimeline"/> covering a time window by enumerating Profile
/// state-span boundaries (which capture both profile switches and CircadianPercentageProfile
/// metadata changes) and eagerly resolving the active schedules per segment.
/// </summary>
/// <remarks>
/// Hot-loop callers (chart-data IOB/COB tick loop, statistics aggregation) build the timeline
/// once per request and resolve sensitivity / carb ratio / basal rate purely in-memory per
/// tick via <see cref="TherapySnapshot"/>'s evaluators. One-shot callers can use
/// <see cref="GetSnapshotAtAsync"/> to retrieve a single snapshot for a specific point in time.
/// </remarks>
public interface ITherapyTimelineResolver
{
    /// <summary>
    /// Build a timeline covering <c>[fromMills, toMills)</c>. The timeline always contains
    /// at least one segment; multiple segments are emitted when Profile state-span boundaries
    /// fall within the window.
    /// </summary>
    /// <param name="fromMills">Window start (inclusive) in Unix milliseconds.</param>
    /// <param name="toMills">Window end (exclusive) in Unix milliseconds.</param>
    /// <param name="specProfile">Optional explicit profile name. When supplied, no profile
    /// switches are honored — the timeline is single-segment with that profile's data.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TherapyTimeline> BuildAsync(
        long fromMills,
        long toMills,
        string? specProfile = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Convenience for one-shot callers: resolves a single <see cref="TherapySnapshot"/> at
    /// the given instant. Equivalent to <see cref="BuildAsync"/> with a degenerate window
    /// <c>[timeMills, timeMills + 1)</c>.
    /// </summary>
    Task<TherapySnapshot> GetSnapshotAtAsync(
        long timeMills,
        string? specProfile = null,
        CancellationToken ct = default
    );
}
