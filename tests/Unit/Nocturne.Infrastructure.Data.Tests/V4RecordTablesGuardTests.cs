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

    private sealed class UnregisteredReading : ISourcedEntity, ISoftDeletable
    {
        public string? DataSource { get; set; }
        public string? Device { get; set; }
        public DateTime? DeletedAt { get; set; }
    }
}
