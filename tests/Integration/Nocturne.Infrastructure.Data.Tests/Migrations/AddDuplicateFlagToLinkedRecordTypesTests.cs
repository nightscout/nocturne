using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddDuplicateFlagToLinkedRecordTypes</c> on a real PostgreSQL as the NOSUPERUSER NOBYPASSRLS
/// migrator under FORCE ROW LEVEL SECURITY: duplicates seeded in two tenants come out flagged exactly
/// where the anti-join hides them, a second backfill writes nothing, a boot killed before the history
/// row re-runs the whole migration cleanly, and Down then Up lands in the same place.
/// </summary>
public class AddDuplicateFlagToLinkedRecordTypesFixture : IAsyncLifetime
{
    private const string Migration = "20261101194922_AddDuplicateFlagToLinkedRecordTypes";
    private const string PriorMigration = "20261031220256_AddTempBasalWatermarkIndex";

    private static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222222");

    private string _migrator = string.Empty;

    internal List<Guid> Expected { get; } = [];
    internal List<Guid> FlaggedAfterUp { get; private set; } = [];
    internal List<string> MismatchesAfterUp { get; private set; } = [];
    internal List<string> VersionsAfterUp { get; private set; } = [];
    internal List<string> VersionsAfterRerun { get; private set; } = [];
    internal List<string> VersionsAfterRestart { get; private set; } = [];
    internal long ColumnsAfterDown { get; private set; }
    internal List<Guid> FlaggedAfterSecondUp { get; private set; } = [];
    internal List<string> MismatchesAfterSecondUp { get; private set; } = [];

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "duplicate_flag_backfill");
        _migrator = database.MigratorConnectionString;

        foreach (var tenant in new[] { TenantA, TenantB })
            Expected.AddRange(await SeedAsync(tenant));

        await MigrateToAsync(null);
        FlaggedAfterUp = await FlaggedAsync();
        MismatchesAfterUp = await MismatchesAsync();
        VersionsAfterUp = await FlaggedVersionsAsync();

        await ExecuteAsync(AddDuplicateFlagToLinkedRecordTypes.BackfillSql);
        VersionsAfterRerun = await FlaggedVersionsAsync();

        await ExecuteAsync($"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{Migration}'");
        await MigrateToAsync(null);
        VersionsAfterRestart = await FlaggedVersionsAsync();

        await MigrateToAsync(PriorMigration);
        ColumnsAfterDown = await ColumnCountAsync();
        await MigrateToAsync(null);
        FlaggedAfterSecondUp = await FlaggedAsync();
        MismatchesAfterSecondUp = await MismatchesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// For each of three record types: a group of a primary and one non-primary copy, and a record
    /// with no link. Inserted through the model, which never writes the flag, so this runs on the
    /// schema before the migration.
    /// </summary>
    /// <returns>The non-primary copies.</returns>
    private async Task<List<Guid>> SeedAsync(Guid tenant)
    {
        await ExecuteAsync($"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', 'dup-{tenant:N}', 'duplicate-flag-backfill', true, now(), now())
            """);

        await using var db = new NocturneDbContext(Options()) { TenantId = tenant };
        var at = DateTime.UtcNow.AddDays(-3);
        var copies = new List<Guid>();

        void Group(RecordType type, Guid primary, Guid copy)
        {
            var canonical = Guid.CreateVersion7();
            db.LinkedRecords.AddRange(Link(tenant, canonical, type, primary, true), Link(tenant, canonical, type, copy, false));
            copies.Add(copy);
        }

        var carbs = Enumerable.Range(0, 3).Select(_ => new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = at, Carbs = 30, DataSource = "a",
        }).ToList();
        var glucose = Enumerable.Range(0, 3).Select(_ => new SensorGlucoseEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = at, Mgdl = 110, DataSource = "a",
        }).ToList();
        var spans = Enumerable.Range(0, 3).Select(_ => new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, Category = "PumpMode", State = "Automatic", StartTimestamp = at,
        }).ToList();
        db.CarbIntakes.AddRange(carbs);
        db.SensorGlucose.AddRange(glucose);
        db.StateSpans.AddRange(spans);
        Group(RecordType.CarbIntake, carbs[0].Id, carbs[1].Id);
        Group(RecordType.SensorGlucose, glucose[0].Id, glucose[1].Id);
        Group(RecordType.StateSpan, spans[0].Id, spans[1].Id);
        await db.SaveChangesAsync();
        return copies;
    }

    private static LinkedRecordEntity Link(Guid tenant, Guid canonical, RecordType type, Guid record, bool primary) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenant, CanonicalId = canonical, RecordType = RecordTypeKeys.Key(type),
        RecordId = record, DataSource = "a", IsPrimary = primary,
    };

    private DbContextOptions<NocturneDbContext> Options() =>
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migrator, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;

    private async Task MigrateToAsync(string? target)
    {
        await using var context = new NocturneDbContext(Options());
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private Task<List<string>> FlaggedRowsAsync(string select) => PerTenantAsync(string.Join(" UNION ALL ",
        AddDuplicateFlagToLinkedRecordTypes.Tables.Select(t => $"SELECT {select} FROM {t.Table} WHERE is_duplicate")));

    private async Task<List<Guid>> FlaggedAsync() => [.. (await FlaggedRowsAsync("id::text")).Select(Guid.Parse)];

    /// <summary>Each flagged row's id and tuple version, which any rewrite of the row changes.</summary>
    private async Task<List<string>> FlaggedVersionsAsync() =>
        [.. (await FlaggedRowsAsync("id::text || '@' || xmin::text || ctid::text")).Order()];

    private Task<List<string>> MismatchesAsync() => PerTenantAsync(string.Join(" UNION ALL ",
        AddDuplicateFlagToLinkedRecordTypes.Tables.Select(t => $"""
            SELECT r.id::text FROM {t.Table} r
            WHERE r.is_duplicate IS DISTINCT FROM EXISTS (
                SELECT 1 FROM linked_records lr
                WHERE lr.tenant_id = r.tenant_id AND lr.record_type = '{t.RecordType}'
                  AND NOT lr.is_primary AND lr.record_id = r.id)
            """)));

    private async Task<long> ColumnCountAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.columns WHERE column_name = 'is_duplicate' AND table_schema = 'public'", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Runs <paramref name="sql"/> as each tenant in turn and gathers its first column.</summary>
    private async Task<List<string>> PerTenantAsync(string sql)
    {
        var rows = new List<string>();
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await using var transaction = await conn.BeginTransactionAsync();
            await using (var pin = new NpgsqlCommand($"SELECT set_config('app.current_tenant_id', '{tenant}', true)", conn))
                await pin.ExecuteNonQueryAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

[Trait("Category", "Integration")]
public class AddDuplicateFlagToLinkedRecordTypesTests(AddDuplicateFlagToLinkedRecordTypesFixture fixture)
    : IClassFixture<AddDuplicateFlagToLinkedRecordTypesFixture>
{
    [Fact]
    public void Up_FlagsTheNonPrimaryCopiesOfEveryTenant()
    {
        fixture.FlaggedAfterUp.Should().BeEquivalentTo(fixture.Expected);
        fixture.MismatchesAfterUp.Should().BeEmpty();
    }

    [Fact]
    public void ASecondBackfill_RewritesNoRow() =>
        fixture.VersionsAfterRerun.Should().Equal(fixture.VersionsAfterUp);

    [Fact]
    public void ABootKilledBeforeTheHistoryRow_ReRunsItCleanly() =>
        fixture.VersionsAfterRestart.Should().Equal(fixture.VersionsAfterUp);

    [Fact]
    public void Down_DropsTheColumns_AndUpRestoresTheFlags()
    {
        fixture.ColumnsAfterDown.Should().Be(0);
        fixture.FlaggedAfterSecondUp.Should().BeEquivalentTo(fixture.Expected);
        fixture.MismatchesAfterSecondUp.Should().BeEmpty();
    }
}
