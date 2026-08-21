using System.Linq.Expressions;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The one predicate for "the rows a data source produced".
/// </summary>
/// <remarks>
/// A row carries two independent handles on its origin (<see cref="ISourcedEntity"/>), and
/// data-source discovery surfaces a list entry under whichever handle it found: glucose and APS
/// discovery group by <c>Device</c>, the non-glucose aggregate groups by <c>DataSource</c>. A
/// caller holding one such identifier therefore cannot know which handle it names, so a lookup or
/// purge has to match either. Matching <c>DataSource</c> alone misses a rig surfaced from APS
/// discovery (its snapshots carry the importing connector's id); requiring <c>DataSource</c> to be
/// null before falling back to <c>Device</c> misses the same rows.
/// </remarks>
public static class SourceFilter
{
    /// <summary>Rows whose <c>DataSource</c> or <c>Device</c> is <paramref name="source"/>.</summary>
    public static Expression<Func<TEntity, bool>> For<TEntity>(string source)
        where TEntity : ISourcedEntity
        => e => e.DataSource == source || e.Device == source;

    /// <summary>Applies <see cref="For{TEntity}"/> to <paramref name="rows"/>.</summary>
    public static IQueryable<TEntity> FromSource<TEntity>(this IQueryable<TEntity> rows, string source)
        where TEntity : ISourcedEntity
        => rows.Where(For<TEntity>(source));
}
