using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

internal static class StateSpanSupersessionExtensions
{
    /// <summary>
    /// Clears <see cref="StateSpanEntity.SupersededById"/> on every live span of the tenant pointing at
    /// one of <paramref name="deletedIds"/>, as the <c>SetNull</c> on that foreign key does for a hard
    /// delete, which a soft delete never fires. Writes by statement and tracks nothing.
    /// </summary>
    /// <remarks>
    /// The foreign key action ran as a referential trigger, which ignores Row Level Security, so the
    /// clear must not be narrowed by the caller's 24 hour history clamp either: a span the clamp hides
    /// would keep pointing at the deleted one. The clamp is lifted for this statement only
    /// (<see cref="HistoryClampLiftExtensions"/>), which writes only the pointer and returns a count.
    /// The pointer and the row's update stamp are written, the stamp so that a history client is sent
    /// the changed row.
    /// </remarks>
    public static async Task<int> ClearSupersededByAsync(
        this NocturneDbContext ctx, IReadOnlyCollection<Guid> deletedIds, CancellationToken ct)
    {
        if (deletedIds.Count == 0)
            return 0;

        var ids = deletedIds.Select(id => (Guid?)id).ToArray();
        var touchedAt = NocturneDbContext.UtcNowAtStoredPrecision();
        return await ctx.WriteWithHistoryClampLiftedAsync(
            token => ctx.StateSpans
                .Where(s => ids.Contains(s.SupersededById))
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(e => e.SupersededById, (Guid?)null)
                        .SetProperty(e => e.UpdatedAt, touchedAt),
                    token),
            ct);
    }
}
