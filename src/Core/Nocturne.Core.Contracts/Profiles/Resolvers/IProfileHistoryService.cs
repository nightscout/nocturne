using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Profiles.Resolvers;

/// <summary>
/// The profile read over a window as a sequence in time, as
/// <see cref="IActiveProfileResolver.GetProfileSpansForRangeAsync"/> cuts it and each stored schedule's
/// versions cut it further.
/// </summary>
public interface IProfileHistoryService
{
    /// <summary>
    /// [<paramref name="fromMs"/>, <paramref name="toMs"/>) as <see cref="ProfileVersion"/>s, and the
    /// <see cref="ProfileChange"/> at each boundary between two of them.
    /// </summary>
    Task<ProfileHistory> GetHistoryAsync(long fromMs, long toMs, CancellationToken ct = default);

    /// <summary>
    /// The scheduled value of each schedule over [<paramref name="fromMs"/>, <paramref name="toMs"/>)
    /// in real time, with the loop's sensitivity and the temporary targets that ran.
    /// </summary>
    /// <param name="fromMs">Window start, in Unix milliseconds.</param>
    /// <param name="toMs">Window end, in Unix milliseconds.</param>
    /// <param name="loop">Whether the caller may read loop cycles; <see cref="ProfileLanes.Loop"/> is null without it.</param>
    /// <param name="temporaryTargets">Whether the caller may read temporary targets; <see cref="ProfileLanes.TemporaryTargets"/> is null without it.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<ProfileLanes> GetLanesAsync(
        long fromMs, long toMs, bool loop, bool temporaryTargets, CancellationToken ct = default);
}
