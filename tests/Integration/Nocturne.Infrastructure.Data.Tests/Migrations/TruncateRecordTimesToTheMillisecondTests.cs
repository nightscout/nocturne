using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>TruncateRecordTimesToTheMillisecond</c> on a real PostgreSQL as the NOSUPERUSER NOBYPASSRLS
/// migrator under FORCE ROW LEVEL SECURITY. Two tenants each hold, in every table it lists, a row whose
/// record times have a part below the millisecond and a row whose times have none: the first comes out
/// truncated, the second is not written, and a second run writes no row.
/// </summary>
public class TruncateRecordTimesToTheMillisecondFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261127173536_AddPeopleViews";

    private static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222222");

    internal static readonly DateTime Whole = new(2026, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc);

    private static readonly HashSet<string> RowStamps =
        ["sys_created_at", "sys_updated_at", "created_at", "updated_at", "deleted_at"];

    private string _migrator = string.Empty;

    internal List<(string Table, string Column)> RecordTimesInSchema { get; private set; } = [];
    internal List<string> UniqueIndexesOverRecordTimes { get; private set; } = [];
    internal List<(Guid Tenant, string Table, Guid Stale, Guid Whole)> Seeded { get; } = [];
    internal List<string> StaleBefore { get; private set; } = [];
    internal List<string> StaleAfterUp { get; private set; } = [];
    internal List<string> NotAtWholeAfterUp { get; private set; } = [];
    internal Dictionary<string, string> VersionsBefore { get; private set; } = [];
    internal Dictionary<string, string> VersionsAfterUp { get; private set; } = [];
    internal Dictionary<string, string> VersionsAfterRerun { get; private set; } = [];

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "record_times_ms");
        _migrator = database.MigratorConnectionString;

        RecordTimesInSchema = await RecordTimesInSchemaAsync();
        UniqueIndexesOverRecordTimes = await UniqueIndexesOverRecordTimesAsync();

        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await NpgsqlTestCommands.SeedTenantAsync(_migrator, tenant);
            await SeedAsync(tenant);
        }

        StaleBefore = await StaleAsync();
        VersionsBefore = await VersionsAsync();

        await using (var context = new NocturneDbContext(TestContexts.MigrationsOptions(_migrator)))
            await context.GetService<IMigrator>().MigrateAsync();
        StaleAfterUp = await StaleAsync();
        NotAtWholeAfterUp = await NotAtWholeAsync();
        VersionsAfterUp = await VersionsAsync();

        await NpgsqlTestCommands.ExecuteAsync(_migrator, TruncateRecordTimesToTheMillisecond.TruncateSql);
        VersionsAfterRerun = await VersionsAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A stale and a whole row of each listed table, written through the model, which stores both at
    /// the millisecond; the stale row's record times are then moved below it by SQL.
    /// </summary>
    private async Task SeedAsync(Guid tenant)
    {
        var rows = new List<(string Table, Guid Stale, Guid Whole)>();
        await using (var db = new NocturneDbContext(TestContexts.MigrationsOptions(_migrator)) { TenantId = tenant })
        {
            foreach (var (table, _) in TruncateRecordTimesToTheMillisecond.Tables)
            {
                var entity = EntityOf(db, table);
                var stale = RecordTableRows.New(entity, tenant, Whole);
                var whole = RecordTableRows.New(entity, tenant, Whole);
                db.AddRange(stale, whole);
                rows.Add((table, ((IIdentified)stale).Id, ((IIdentified)whole).Id));
            }

            await db.SaveChangesAsync();
        }

        await using var conn = await NpgsqlTestCommands.OpenAsync(_migrator);
        await using var transaction = await conn.BeginTransactionAsync();
        await conn.PinTenantAsync(tenant, local: true);
        foreach (var (table, stale, whole) in rows)
        {
            var columns = ColumnsOf(table);
            await conn.ExecuteAsync(
                $"UPDATE {table} SET {string.Join(", ", columns.Select(c => $"\"{c}\" = \"{c}\" + interval '456 microseconds'"))} WHERE id = @id",
                ("id", stale));
            Seeded.Add((tenant, table, stale, whole));
        }

        await transaction.CommitAsync();
    }

    private static IEntityType EntityOf(NocturneDbContext db, string table) =>
        db.Model.GetEntityTypes().Single(e => e.GetTableName() == table && e.GetViewName() is null);

    private static string[] ColumnsOf(string table) =>
        TruncateRecordTimesToTheMillisecond.Tables.Single(t => t.Table == table).Columns;

    /// <summary>
    /// Every time column of a V4 record table in the schema the migration runs on, but the row stamps.
    /// </summary>
    private async Task<List<(string Table, string Column)>> RecordTimesInSchemaAsync()
    {
        await using var db = new NocturneDbContext(TestContexts.MigrationsOptions(_migrator));
        var tables = V4RecordTables.All.Select(t => db.Model.FindEntityType(t.Entity)!.GetTableName()!).ToArray();
        var columns = await NpgsqlTestCommands.QueryAsync(
            _migrator,
            """
            SELECT table_name, column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND data_type = 'timestamp with time zone' AND table_name = ANY(@tables)
            """,
            r => (r.GetString(0), r.GetString(1)),
            ("tables", tables));
        return [.. columns.Where(c => !RowStamps.Contains(c.Item2))];
    }

    /// <summary>The unique indexes and exclusion constraints of the listed tables that hold a listed column.</summary>
    private Task<List<string>> UniqueIndexesOverRecordTimesAsync() =>
        NpgsqlTestCommands.QueryAsync(
            _migrator,
            """
            SELECT DISTINCT i.indexrelid::regclass::text
            FROM pg_index i
            JOIN pg_class t ON t.oid = i.indrelid
            JOIN pg_attribute a ON a.attrelid = i.indrelid
            JOIN unnest(@tables, @columns) AS listed(tbl, col) ON listed.tbl = t.relname AND listed.col = a.attname
            WHERE (i.indisunique OR i.indisexclusion)
              AND (a.attnum = ANY(i.indkey)
                   OR pg_get_expr(i.indexprs, i.indrelid) LIKE '%' || quote_ident(a.attname) || '%'
                   OR pg_get_expr(i.indpred, i.indrelid) LIKE '%' || quote_ident(a.attname) || '%')
            """,
            r => r.GetString(0),
            ("tables", TruncateRecordTimesToTheMillisecond.Tables.SelectMany(t => t.Columns.Select(_ => t.Table)).ToArray()),
            ("columns", TruncateRecordTimesToTheMillisecond.Tables.SelectMany(t => t.Columns).ToArray()));

    /// <summary>Each listed column of each seeded row that holds a part below the millisecond.</summary>
    private Task<List<string>> StaleAsync() => PerTenantAsync(string.Join(" UNION ALL ",
        TruncateRecordTimesToTheMillisecond.Tables.SelectMany(t => t.Columns.Select(c =>
            $"SELECT '{t.Table}.{c}:' || id::text FROM {t.Table} WHERE \"{c}\" <> date_trunc('milliseconds', \"{c}\")"))));

    /// <summary>Each listed column of each seeded row that is not at <see cref="Whole"/>.</summary>
    private Task<List<string>> NotAtWholeAsync() => PerTenantAsync(string.Join(" UNION ALL ",
        TruncateRecordTimesToTheMillisecond.Tables.SelectMany(t => t.Columns.Select(c =>
            $"SELECT '{t.Table}.{c}:' || id::text FROM {t.Table} WHERE \"{c}\" IS DISTINCT FROM timestamptz '2026-03-04 05:06:07.123+00'"))));

    /// <summary>Each seeded row's tuple version, which any write of the row changes, by table and id.</summary>
    private async Task<Dictionary<string, string>> VersionsAsync() =>
        (await PerTenantAsync(string.Join(" UNION ALL ", TruncateRecordTimesToTheMillisecond.Tables.Select(t =>
            $"SELECT '{t.Table}:' || id::text || '=' || xmin::text || ctid::text FROM {t.Table}"))))
        .Select(v => v.Split('='))
        .ToDictionary(v => v[0], v => v[1]);

    /// <summary>Runs <paramref name="sql"/> as each tenant in turn and gathers its first column.</summary>
    private async Task<List<string>> PerTenantAsync(string sql)
    {
        var rows = new List<string>();
        await using var conn = await NpgsqlTestCommands.OpenAsync(_migrator);
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await using var transaction = await conn.BeginTransactionAsync();
            await conn.PinTenantAsync(tenant, local: true);
            rows.AddRange(await conn.QueryAsync(sql, r => r.GetString(0)));
        }

        return rows;
    }
}

[Trait("Category", "Integration")]
public class TruncateRecordTimesToTheMillisecondTests(TruncateRecordTimesToTheMillisecondFixture fixture)
    : IClassFixture<TruncateRecordTimesToTheMillisecondFixture>
{
    private static string Key(string table, Guid id) => $"{table}:{id}";

    [Fact]
    public void ItListsEveryRecordTimeOfEveryRecordTable() =>
        TruncateRecordTimesToTheMillisecond.Tables
            .SelectMany(t => t.Columns.Select(c => (t.Table, c)))
            .Should().BeEquivalentTo(fixture.RecordTimesInSchema);

    [Fact]
    public void NoUniqueIndexHoldsARecordTime_SoNoTruncationCollides() =>
        fixture.UniqueIndexesOverRecordTimes.Should().BeEmpty();

    [Fact]
    public void EveryTenantStartsWithAStaleRowInEveryListedColumn() =>
        fixture.StaleBefore.Should().HaveCount(
            2 * TruncateRecordTimesToTheMillisecond.Tables.Sum(t => t.Columns.Length));

    [Fact]
    public void Up_TruncatesEveryStaleRowOfEveryTenant()
    {
        fixture.StaleAfterUp.Should().BeEmpty();
        fixture.NotAtWholeAfterUp.Should().BeEmpty();
        fixture.Seeded.Should().AllSatisfy(row =>
            fixture.VersionsAfterUp[Key(row.Table, row.Stale)].Should().NotBe(
                fixture.VersionsBefore[Key(row.Table, row.Stale)], $"{row.Table} {row.Stale} was stale"));
    }

    [Fact]
    public void Up_LeavesEveryWholeRowUnwritten() =>
        fixture.Seeded.Should().AllSatisfy(row =>
            fixture.VersionsAfterUp[Key(row.Table, row.Whole)].Should().Be(
                fixture.VersionsBefore[Key(row.Table, row.Whole)], $"{row.Table} {row.Whole} was whole"));

    [Fact]
    public void ASecondRun_WritesNoRow() =>
        fixture.VersionsAfterRerun.Should().Equal(fixture.VersionsAfterUp);
}
