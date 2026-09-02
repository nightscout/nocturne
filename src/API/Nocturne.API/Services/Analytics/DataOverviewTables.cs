using System.Linq.Expressions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// One table behind the data overview, described in the terms its surfaces need: the
/// <see cref="SyncDataType"/> name per-day counts are keyed by, the domain timestamp, the source
/// handle, and the <see cref="RecordType"/> whose non-primary <see cref="LinkedRecord"/>s stand for
/// cross-connector duplicates.
/// </summary>
/// <seealso cref="DataOverviewTables"/>
internal interface IDataOverviewTable
{
    /// <summary>Key this table's rows are counted under in <see cref="Core.Models.Services.DailySummaryDay.Counts"/>.</summary>
    string CountsKey { get; }

    /// <summary>Null when the table takes no part in deduplication.</summary>
    RecordType? DedupRecordType { get; }

    /// <summary>
    /// Every row's timestamp, with neither a source filter nor duplicate exclusion applied — the
    /// overview's year range spans whatever the tenant holds.
    /// </summary>
    IQueryable<DateTime?> Timestamps(NocturneDbContext context);

    /// <summary>Null when the overview does not attribute this table to a data source.</summary>
    IQueryable<string>? Sources(NocturneDbContext context);

    /// <summary>
    /// Timestamps within the half-open UTC interval, less any row whose id is in
    /// <paramref name="nonPrimaryIds"/>. Null when <paramref name="dataSources"/> constrains a
    /// table the overview does not attribute to a data source.
    /// </summary>
    IQueryable<DateTime>? TimestampsInRange(
        NocturneDbContext context,
        DateTime startUtc,
        DateTime endUtc,
        string[]? dataSources,
        IQueryable<Guid>? nonPrimaryIds
    );
}

/// <inheritdoc cref="IDataOverviewTable"/>
/// <typeparam name="TEntity">The mapped entity behind the table.</typeparam>
/// <param name="countsKey">Names the key <see cref="CountsKey"/> reports.</param>
/// <param name="table">Leases the table's <see cref="Microsoft.EntityFrameworkCore.DbSet{TEntity}"/> from a context.</param>
/// <param name="timestamp">The column the overview treats as the row's moment in time.</param>
/// <param name="id">The column <see cref="LinkedRecord.RecordId"/> refers to.</param>
/// <param name="source">The column holding the data-source handle, or null when the table has none.</param>
/// <param name="dedupRecordType">Names the record type as deduplication knows it, or null.</param>
internal sealed class DataOverviewTable<TEntity>(
    SyncDataType countsKey,
    Func<NocturneDbContext, IQueryable<TEntity>> table,
    Expression<Func<TEntity, DateTime>> timestamp,
    Expression<Func<TEntity, Guid>> id,
    Expression<Func<TEntity, string?>>? source,
    RecordType? dedupRecordType = null
) : IDataOverviewTable
    where TEntity : class
{
    /// <inheritdoc />
    public string CountsKey { get; } = countsKey.ToString();

    /// <inheritdoc />
    public RecordType? DedupRecordType { get; } = dedupRecordType;

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
        string[]? dataSources,
        IQueryable<Guid>? nonPrimaryIds
    )
    {
        var query = table(context).Where(Compose(timestamp, t => t >= startUtc && t < endUtc));

        if (dataSources is { Length: > 0 } wanted)
        {
            if (source is null)
                return null;

            query = query.Where(Compose(source, s => wanted.Contains(s!)));
        }

        if (nonPrimaryIds is { } duplicates)
            query = query.Where(Compose(id, recordId => !duplicates.Contains(recordId)));

        return query.Select(timestamp);
    }

    /// <summary>
    /// Reads <paramref name="predicate"/> against the column <paramref name="selector"/> picks, so
    /// the resulting tree is the one a hand-written lambda over that column would have produced and
    /// its captured values still reach the provider as query parameters.
    /// </summary>
    private static Expression<Func<TEntity, bool>> Compose<TValue>(
        Expression<Func<TEntity, TValue>> selector,
        Expression<Func<TValue, bool>> predicate
    ) =>
        Expression.Lambda<Func<TEntity, bool>>(
            Substitute(predicate.Body, predicate.Parameters[0], selector.Body),
            selector.Parameters
        );

    /// <summary>
    /// Applies <paramref name="projection"/> to the column <paramref name="selector"/> picks, on the
    /// same terms as <see cref="Compose{TValue}"/>.
    /// </summary>
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
/// Every table the data overview aggregates, driving its year range, its data-source list and its
/// per-day counts. Order is significant: it fixes the insertion order of
/// <see cref="Core.Models.Services.DailySummaryDay.Counts"/>, which survives into the serialized
/// response.
/// </summary>
internal static class DataOverviewTables
{
    internal static readonly IReadOnlyList<IDataOverviewTable> All =
    [
        new DataOverviewTable<SensorGlucoseEntity>(
            SyncDataType.Glucose,
            c => c.SensorGlucose, e => e.Timestamp, e => e.Id, e => e.DataSource,
            RecordType.SensorGlucose),
        new DataOverviewTable<MeterGlucoseEntity>(
            SyncDataType.ManualBG,
            c => c.MeterGlucose, e => e.Timestamp, e => e.Id, e => e.DataSource),
        new DataOverviewTable<BolusEntity>(
            SyncDataType.Boluses,
            c => c.Boluses, e => e.Timestamp, e => e.Id, e => e.DataSource,
            RecordType.Bolus),
        new DataOverviewTable<CarbIntakeEntity>(
            SyncDataType.CarbIntake,
            c => c.CarbIntakes, e => e.Timestamp, e => e.Id, e => e.DataSource,
            RecordType.CarbIntake),
        new DataOverviewTable<BolusCalculationEntity>(
            SyncDataType.BolusCalculations,
            c => c.BolusCalculations, e => e.Timestamp, e => e.Id, e => e.DataSource,
            RecordType.BolusCalculation),
        new DataOverviewTable<NoteEntity>(
            SyncDataType.Notes,
            c => c.Notes, e => e.Timestamp, e => e.Id, e => e.DataSource,
            RecordType.Note),
        new DataOverviewTable<DeviceEventEntity>(
            SyncDataType.DeviceEvents,
            c => c.DeviceEvents, e => e.Timestamp, e => e.Id, e => e.DataSource,
            RecordType.DeviceEvent),
        new DataOverviewTable<StateSpanEntity>(
            SyncDataType.StateSpans,
            c => c.StateSpans, e => e.StartTimestamp, e => e.Id, e => e.Source,
            RecordType.StateSpan),
        // ApsSnapshots carries a DataSource column the overview does not surface, so a source
        // filter drops the table rather than narrowing it.
        new DataOverviewTable<ApsSnapshotEntity>(
            SyncDataType.DeviceStatus,
            c => c.ApsSnapshots, e => e.Timestamp, e => e.Id, source: null),
        new DataOverviewTable<BGCheckEntity>(
            SyncDataType.BGChecks,
            c => c.BGChecks, e => e.Timestamp, e => e.Id, e => e.DataSource,
            RecordType.BGCheck),
        new DataOverviewTable<TempBasalEntity>(
            SyncDataType.TempBasals,
            c => c.TempBasals, e => e.StartTimestamp, e => e.Id, e => e.DataSource,
            RecordType.TempBasal),
    ];
}
