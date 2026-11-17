using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>The order a time-window read of a V4 record table serves its rows in.</summary>
public static class ObservationOrder
{
    /// <summary>
    /// By <see cref="IObservationTimestamped.Timestamp"/>, rows at one instant by id in the same
    /// direction, so a limited read keeps the same rows on every request and a keyset cursor on
    /// (timestamp, id) resumes it.
    /// </summary>
    public static IOrderedQueryable<TEntity> InObservationOrder<TEntity>(this IQueryable<TEntity> query, bool descending)
        where TEntity : IObservationTimestamped, IIdentified =>
        descending
            ? query.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id)
            : query.OrderBy(e => e.Timestamp).ThenBy(e => e.Id);
}
