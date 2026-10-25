using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Profiles.Resolvers;

/// <summary>
/// The profiles read over a window, as <see cref="IActiveProfileResolver.GetProfileSpansForRangeAsync"/>
/// cuts it, with the schedules each read then.
/// </summary>
public interface IActiveProfileSchedulesService
{
    /// <summary>
    /// One entry per profile read over [<paramref name="fromMs"/>, <paramref name="toMs"/>), the
    /// longest read first.
    /// </summary>
    Task<IReadOnlyList<ActiveProfileSchedules>> GetForRangeAsync(
        long fromMs, long toMs, CancellationToken ct = default);
}
