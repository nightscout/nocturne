using System.Text.RegularExpressions;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Every entity a source can own is in <see cref="V4RecordTables"/>. A record table left out of the
/// registry is left out of every list over the record tables at once.
/// </summary>
[Trait("Category", "Unit")]
public class V4RecordTablesGuardTests
{
    private static readonly Type[] ContextEntities = typeof(NocturneDbContext).GetProperties()
        .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
        .Select(p => p.PropertyType.GetGenericArguments()[0])
        .Distinct()
        .ToArray();

    private static readonly Type[] Registered = [.. V4RecordTables.All.Select(t => t.Entity)];

    private static bool IsSourcedRecord(Type entity) =>
        typeof(IV4TimeSeriesEntity).IsAssignableFrom(entity)
        || (typeof(ISourcedEntity).IsAssignableFrom(entity) && typeof(ISoftDeletable).IsAssignableFrom(entity));

    private static List<Type> Unaccounted(IEnumerable<Type> entities, IEnumerable<Type> registered) =>
        [.. entities.Where(IsSourcedRecord).Except(registered)];

    [Fact]
    public void EverySourcedRecordEntityIsRegistered()
    {
        Unaccounted(ContextEntities, Registered).Should().BeEmpty(
            "a sourced, soft-deletable entity is a V4 record table every per-table list must see");
    }

    [Fact]
    public void TheGuardReportsAnEntityThatIsNotRegistered()
    {
        Unaccounted([.. ContextEntities, typeof(UnregisteredReading)], Registered)
            .Should().Equal(typeof(UnregisteredReading));
        Unaccounted(ContextEntities, Registered.Where(e => e != typeof(BolusEntity)))
            .Should().Equal(typeof(BolusEntity));
    }

    [Fact]
    public void TheRegistryNamesEachTableOnceUnderItsOwnDbSet()
    {
        Registered.Should().OnlyHaveUniqueItems();
        foreach (var table in V4RecordTables.All)
            typeof(NocturneDbContext).GetProperty(table.Name)!.PropertyType.GetGenericArguments()
                .Should().Equal(table.Entity);
    }

    [Fact]
    public void EveryListExemptionNamesARegisteredTable()
    {
        Registered.Should().Contain(V4RecordTables.Uncounted.Keys);
        Registered.Should().Contain(V4RecordTables.UnattributedInOverview.Keys);
        V4RecordTables.Uncounted.Values.Concat(V4RecordTables.UnattributedInOverview.Values)
            .Should().OnlyContain(reason => !string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void APurgeReadsEveryDeviceStatusTableBeforeItDeletesAStatusExtrasRow()
    {
        using var context = OfflineDbContext.Create();
        var sql = V4RecordTables.StatusExtrasLeftBy(context, "src").ToQueryString();

        foreach (var table in V4RecordTables.OfKind(V4RecordKind.DeviceStatus))
        {
            var name = context.Model.FindEntityType(table.Entity)!.GetTableName()!;
            Regex.IsMatch(sql, HeldBySource(name), RegexOptions.Singleline).Should().BeTrue(
                $"a status with a row of the source in {name}, deleted or not, is a status of the source");
            Regex.IsMatch(sql, LiveIn(name), RegexOptions.Singleline).Should().BeTrue(
                $"a live row of any source in {name} keeps its status live");
        }
    }

    [Fact]
    public void APurgeFindsTheSourceStatusesOfAnExtrasRowByItsOwnCorrelationId()
    {
        using var context = OfflineDbContext.Create();
        var sql = V4RecordTables.StatusExtrasLeftBy(context, "src").ToQueryString();

        sql.Should().NotContain("UNION").And.NotContain(" IN (",
            "each chunk of a purge reruns this query, and a set of the source's statuses is rebuilt from its whole history every time");
        var tables = V4RecordTables.OfKind(V4RecordKind.DeviceStatus).Count();
        Regex.Matches(sql, HeldBySource(@"\w+"), RegexOptions.Singleline).Should().HaveCount(tables);
        Regex.Matches(sql, @"\) OR EXISTS \(").Should().HaveCount(tables - 1);
    }

    private static string HeldBySource(string table) =>
        $@"(?<!NOT )EXISTS \(\s*SELECT 1\s*FROM {table} AS (\w+)\s*WHERE (?:(?!EXISTS).)*?@source(?:(?!EXISTS).)*?\1\.correlation_id = d\.correlation_id\)";

    private static string LiveIn(string table) =>
        $@"NOT EXISTS \(\s*SELECT 1\s*FROM {table} AS (\w+)\s*WHERE (?:(?!EXISTS).)*?\1\.correlation_id = d\.correlation_id AND \1\.deleted_at IS NULL\)";

    private sealed class UnregisteredReading : ISourcedEntity, ISoftDeletable
    {
        public string? DataSource { get; set; }
        public string? Device { get; set; }
        public DateTime? DeletedAt { get; set; }
    }
}
