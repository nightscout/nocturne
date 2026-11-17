using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Repositories;

/// <summary>
/// The publishing-side dedup read of <see cref="Activity"/> records. An activity is stored as a
/// state span, heart rate, step count or sleep session, each carrying the activity's id as its
/// <c>OriginalId</c>.
/// </summary>
/// <seealso cref="Activity"/>
public interface IActivityDedupRepository
{
    /// <summary>
    /// The ids among <paramref name="activityIds"/> a re-upload would find held by any of the four
    /// tables, from any source: by a live row, or by one the user deleted. A sleep session counts
    /// only when the activity stored it, so a session of another source under the same id does not
    /// hold it.
    /// </summary>
    Task<IReadOnlySet<string>> GetHeldIdsAsync(
        IReadOnlyCollection<string> activityIds, CancellationToken ct = default);
}
