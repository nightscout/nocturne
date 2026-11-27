using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// A source's count reads one table as the disjoint parts of <c>SourceFilter.Parts</c>: an equality on
/// <c>(tenant_id, data_source)</c> and ranges of <c>(tenant_id, device, data_source)</c>. The count is
/// index-served only when every part is, so each table the count reads that way needs both indexes.
/// The tables are found from the SQL the count sends, not from the marker the index convention reads.
/// </summary>
[Trait("Category", "Unit")]
public partial class SourceCountIndexTests
{
    [Fact]
    public void EveryTableTheCountReadsByDeviceAndSource_HasTheIndexOfEveryPart()
    {
        using var db = OfflineDbContext.Create();

        var reads = V4RecordTables.All.ToDictionary(t => t, t => CountSql(db, t));
        var byDevice = reads.Where(r => DeviceTest().IsMatch(r.Value)).Select(r => r.Key).ToList();

        byDevice.Should().HaveCountGreaterThan(20, "an empty set would let the index checks below pass vacuously");
        reads.Keys.Except(byDevice).Select(t => t.Entity)
            .Should().Equal([typeof(StateSpanEntity)], "a table whose count reads no device column needs no device index");

        byDevice
            .Where(t => !HasDeviceSourceIndex(db, t) || !HasDataSourcePartIndex(db, t))
            .Select(t => t.Entity.Name)
            .Should().BeEmpty(
                "a count part with no index of its own reads the tenant's whole partition of the table");
    }

    [Fact]
    public void TheCheckSeesATableWithoutTheDeviceSourceIndex()
    {
        using var db = OfflineDbContext.Create();
        var table = V4RecordTables.All.Single(t => t.Entity == typeof(StateSpanEntity));

        HasDeviceSourceIndex(db, table).Should().BeFalse();
        HasDataSourcePartIndex(db, table).Should().BeFalse();
    }

    private static string CountSql(NocturneDbContext db, V4RecordTable table) =>
        ((IQueryable)table.GetType().GetMethod(nameof(V4RecordTable<StateSpanEntity>.ReadOwned))!
            .Invoke(table, [db, "source"])!).ToQueryString();

    private static bool HasDeviceSourceIndex(NocturneDbContext db, V4RecordTable table)
    {
        var entity = db.Model.FindEntityType(table.Entity)!;
        return entity.GetIndexes().Any(i =>
            i.GetDatabaseName() == $"ix_{entity.GetTableName()}_tenant_device_source"
            && Columns(i).SequenceEqual([nameof(ITenantScoped.TenantId), nameof(ISourcedEntity.Device), nameof(ISourcedEntity.DataSource)])
            && !i.IsUnique
            && i.GetFilter() == "deleted_at IS NULL");
    }

    private static bool HasDataSourcePartIndex(NocturneDbContext db, V4RecordTable table) =>
        db.Model.FindEntityType(table.Entity)!.GetIndexes().Any(i =>
            Columns(i).Take(2).SequenceEqual([nameof(ITenantScoped.TenantId), nameof(ISourcedEntity.DataSource)])
            && !i.IsUnique
            && i.GetFilter() is null or "deleted_at IS NULL");

    private static IEnumerable<string> Columns(IIndex index) => index.Properties.Select(p => p.Name);

    [GeneratedRegex(@"\.device = ")]
    private static partial Regex DeviceTest();
}
