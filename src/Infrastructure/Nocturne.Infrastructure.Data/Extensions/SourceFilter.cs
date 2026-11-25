using System.Linq.Expressions;
using Nocturne.Core.Models.Connectors;
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

    /// <summary>
    /// The rows of <see cref="For{TEntity}"/> as disjoint parts, each one range of an index: the rows
    /// whose <c>DataSource</c> is <paramref name="source"/>, then those only their <c>Device</c> names,
    /// split by where their <c>DataSource</c> sorts.
    /// </summary>
    /// <remarks>
    /// A read of a source's rows unions these. The planner sizes the <c>DataSource OR Device</c> test
    /// from the whole table's statistics, not the tenant's, so a source that is common across the
    /// instance and absent from the tenant reads the tenant's every row. Each part here is an equality on
    /// <c>(tenant_id, data_source)</c> or a range of <c>(tenant_id, device, data_source)</c>, so a source
    /// the tenant holds none of reads nothing. The <c>Device</c> parts compare <c>DataSource</c> with
    /// <c>&lt;</c> and <c>&gt;</c> rather than <c>&lt;&gt;</c> because only a range bounds an index scan, and
    /// test it for null because <see cref="string.Compare(string, string)"/> orders null first where SQL
    /// compares it as unknown.
    /// </remarks>
    public static IReadOnlyList<Expression<Func<TEntity, bool>>> Parts<TEntity>(string source)
        where TEntity : ISourcedEntity =>
    [
        e => e.DataSource == source,
        e => e.Device == source && e.DataSource == null,
        e => e.Device == source && e.DataSource != null && string.Compare(e.DataSource, source) < 0,
        e => e.Device == source && e.DataSource != null && string.Compare(e.DataSource, source) > 0,
    ];

    /// <summary>
    /// Rows one of <paramref name="grantIds"/> inserted.
    /// </summary>
    public static Expression<Func<TEntity, bool>> WrittenBy<TEntity>(IReadOnlyList<Guid> grantIds)
        where TEntity : IWriterStamped
        => e => e.WriterGrantId != null && grantIds.Contains(e.WriterGrantId.Value);

    /// <summary>
    /// The rows <paramref name="selector"/> names, with <paramref name="source"/> as the predicate of a
    /// source: one test on the row, for a query correlated with another row.
    /// </summary>
    public static Expression<Func<TEntity, bool>> For<TEntity>(
        RecordSelector selector, Func<string, Expression<Func<TEntity, bool>>> source)
        where TEntity : IWriterStamped
        => selector switch
        {
            RecordSelector.BySource s => source(s.Source),
            RecordSelector.ByWriters w => WrittenBy<TEntity>(w.GrantIds),
            _ => throw new ArgumentOutOfRangeException(nameof(selector), selector, null),
        };

    /// <summary>
    /// The rows of <paramref name="rows"/> that <paramref name="selector"/> names: a source's as the
    /// union of its <paramref name="sourceParts"/>, so each part is read off its own index as a read of
    /// <see cref="Parts{TEntity}"/> is, and a set of writers' by <see cref="WrittenBy{TEntity}"/>.
    /// </summary>
    public static IQueryable<TEntity> Selected<TEntity>(
        this IQueryable<TEntity> rows,
        RecordSelector selector,
        Func<string, IReadOnlyList<Expression<Func<TEntity, bool>>>> sourceParts)
        where TEntity : IWriterStamped
        => selector switch
        {
            RecordSelector.BySource s => sourceParts(s.Source).Select(rows.Where).Aggregate(Queryable.Concat),
            RecordSelector.ByWriters w => rows.Where(WrittenBy<TEntity>(w.GrantIds)),
            _ => throw new ArgumentOutOfRangeException(nameof(selector), selector, null),
        };

    /// <summary>Applies <see cref="Selected{TEntity}(IQueryable{TEntity}, RecordSelector, Func{string, IReadOnlyList{Expression{Func{TEntity, bool}}}})"/> with <see cref="Parts{TEntity}"/>.</summary>
    public static IQueryable<TEntity> Selected<TEntity>(this IQueryable<TEntity> rows, RecordSelector selector)
        where TEntity : ISourcedEntity, IWriterStamped
        => rows.Selected(selector, Parts<TEntity>);
}
