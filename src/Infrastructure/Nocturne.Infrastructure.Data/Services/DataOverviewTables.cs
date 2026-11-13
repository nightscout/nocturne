using System.Linq.Expressions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// One table behind the data overview, in the terms its year-range, data-source and
/// per-day-count surfaces need.
/// </summary>
/// <seealso cref="DataOverviewTables"/>
public interface IDataOverviewTable
{
    string CountsKey { get; }

    /// <summary>
    /// Every row's timestamp, with neither a source filter nor duplicate exclusion applied — the
    /// overview's year range spans whatever the tenant holds.
    /// </summary>
    IQueryable<DateTime?> Timestamps(NocturneDbContext context);

    /// <summary>Null when the overview does not attribute this table to a data source.</summary>
    IQueryable<string>? Sources(NocturneDbContext context);

    /// <summary>
    /// Timestamps within the half-open UTC interval, less the duplicate copies
    /// <see cref="ReadVisibilityFilter"/> drops. Null when <paramref name="dataSources"/> constrains a
    /// table the overview does not attribute to a data source.
    /// </summary>
    IQueryable<DateTime>? TimestampsInRange(
        NocturneDbContext context,
        DateTime startUtc,
        DateTime endUtc,
        string[]? dataSources
    );
}

/// <inheritdoc cref="IDataOverviewTable"/>
internal sealed class DataOverviewTable<TEntity>(
    string countsKey,
    Func<NocturneDbContext, IQueryable<TEntity>> table,
    Expression<Func<TEntity, DateTime>> timestamp,
    Expression<Func<TEntity, string?>>? source
) : IDataOverviewTable
    where TEntity : class, IIdentified
{
    /// <inheritdoc />
    public string CountsKey { get; } = countsKey;

    /// <inheritdoc />
    public IQueryable<DateTime?> Timestamps(NocturneDbContext context) =>
        table(context).Select(Project(timestamp, t => (DateTime?)t));

    /// <inheritdoc />
    public IQueryable<string>? Sources(NocturneDbContext context) =>
        source is null
            ? null
            : table(context)
                .Where(Compose(source, s => s != null))
                .Select(Project(source, s => s!));

    /// <inheritdoc />
    public IQueryable<DateTime>? TimestampsInRange(
        NocturneDbContext context,
        DateTime startUtc,
        DateTime endUtc,
        string[]? dataSources
    )
    {
        var query = table(context).Where(Compose(timestamp, t => t >= startUtc && t < endUtc));

        if (dataSources is { Length: > 0 } wanted)
        {
            if (source is null)
                return null;

            query = query.Where(Compose(source, s => wanted.Contains(s!)));
        }

        return query.ExcludeDuplicatesWhereFlagged().Select(timestamp);
    }

    /// <summary>
    /// Substituting into a written lambda, rather than assembling the tree node by node, keeps the
    /// values that lambda captures reaching the provider as query parameters.
    /// </summary>
    private static Expression<Func<TEntity, bool>> Compose<TValue>(
        Expression<Func<TEntity, TValue>> selector,
        Expression<Func<TValue, bool>> predicate
    ) =>
        Expression.Lambda<Func<TEntity, bool>>(
            Substitute(predicate.Body, predicate.Parameters[0], selector.Body),
            selector.Parameters
        );

    private static Expression<Func<TEntity, TResult>> Project<TValue, TResult>(
        Expression<Func<TEntity, TValue>> selector,
        Expression<Func<TValue, TResult>> projection
    ) =>
        Expression.Lambda<Func<TEntity, TResult>>(
            Substitute(projection.Body, projection.Parameters[0], selector.Body),
            selector.Parameters
        );

    private static Expression Substitute(
        Expression body,
        ParameterExpression parameter,
        Expression replacement
    ) => new ParameterSubstitution(parameter, replacement).Visit(body);

    private sealed class ParameterSubstitution(ParameterExpression parameter, Expression replacement)
        : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == parameter ? replacement : base.VisitParameter(node);
    }
}

/// <summary>
/// Every table the data overview aggregates (<see cref="V4RecordTables.Overview"/>), driving its year
/// range, its data-source list and its per-day counts.
/// </summary>
public static class DataOverviewTables
{
    public static readonly IReadOnlyList<IDataOverviewTable> All =
    [
        .. V4RecordTables.Overview.Select(t =>
            t.ToOverview(attributed: !V4RecordTables.UnattributedInOverview.ContainsKey(t.Table.Entity))),
    ];
}
