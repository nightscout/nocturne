using Microsoft.Extensions.Time.Testing;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Every V4 record table stores the times of its record at the millisecond, on insert and on update,
/// and the stamps of its row at the microsecond. The store is SQLite, which keeps every tick, so a time
/// the save does not cut reads back with its part below the millisecond.
/// </summary>
[Trait("Category", "Unit")]
public class RecordTimesAtTheMillisecondTests : IDisposable
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTime Written = new DateTime(2026, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc).AddTicks(4567);
    private static readonly DateTime Edited = new DateTime(2026, 3, 4, 6, 0, 0, 987, DateTimeKind.Utc).AddTicks(6543);
    private static readonly DateTime Now = new DateTime(2026, 3, 5, 0, 0, 0, 456, DateTimeKind.Utc).AddTicks(7891);

    private static readonly HashSet<string> RowStamps = ["SysCreatedAt", "SysUpdatedAt", "CreatedAt", "UpdatedAt", "DeletedAt"];

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(Tenant);

    public void Dispose() => _db.Dispose();

    public static TheoryData<string> Tables => [.. V4RecordTables.All.Select(t => t.Name)];

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task AnInsertStoresEachRecordTimeAtTheMillisecond(string table)
    {
        var (entity, id) = await InsertAsync(table);

        await using var db = Context();
        var stored = await RecordTableRows.FindAsync(db, db.Model.FindEntityType(entity)!, id);

        foreach (var column in RecordTableRows.TimeColumns(db.Model.FindEntityType(entity)!))
        {
            var value = column.PropertyInfo!.GetValue(stored);
            switch (column.Name)
            {
                case "DeletedAt":
                    value.Should().Be(Written, $"{table}.{column.Name} is a row stamp the caller set");
                    break;
                case var stamp when RowStamps.Contains(stamp):
                    value.Should().Be(new DateTime(2026, 3, 5, 0, 0, 0, 456, DateTimeKind.Utc).AddTicks(7890),
                        $"{table}.{column.Name} is a row stamp, kept at the microsecond");
                    break;
                default:
                    value.Should().Be(new DateTime(2026, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc),
                        $"{table}.{column.Name} is a time of the record");
                    break;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task AnUpdateStoresEachRecordTimeAtTheMillisecond(string table)
    {
        var (entity, id) = await InsertAsync(table);
        var recordTimes = new List<string>();

        await using (var db = Context())
        {
            var row = await RecordTableRows.FindAsync(db, db.Model.FindEntityType(entity)!, id);
            var entry = db.Attach(row);
            foreach (var column in RecordTableRows.TimeColumns(entry.Metadata).Where(c => !RowStamps.Contains(c.Name)))
            {
                entry.Property(column.Name).CurrentValue = Edited;
                recordTimes.Add(column.Name);
            }

            await db.SaveChangesAsync();
        }

        await using var reader = Context();
        var stored = await RecordTableRows.FindAsync(reader, reader.Model.FindEntityType(entity)!, id);
        foreach (var name in recordTimes)
            stored.GetType().GetProperty(name)!.GetValue(stored).Should().Be(
                new DateTime(2026, 3, 4, 6, 0, 0, 987, DateTimeKind.Utc), $"{table}.{name} is a time of the record");
    }

    private async Task<(Type Entity, Guid Id)> InsertAsync(string table)
    {
        var entity = V4RecordTables.All.Single(t => t.Name == table).Entity;
        await using var db = Context();
        var row = RecordTableRows.New(db.Model.FindEntityType(entity)!, Tenant, Written);
        db.Add(row);
        await db.SaveChangesAsync();
        return (entity, ((IIdentified)row).Id);
    }

    private NocturneDbContext Context()
    {
        var db = _db.CreateContext();
        db.Clock = new FakeTimeProvider(new DateTimeOffset(Now));
        return db;
    }
}
