using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Integration.Migration;

/// <summary>
/// Runs <c>RepairEpochProfileVersions</c> over the profile stores of two tenants on a real
/// PostgreSQL, then down and up again.
/// </summary>
public sealed class RepairEpochProfileVersionsFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261021165405_AddTempBasalRelativeIndex";

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111112377");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222377");

    internal static readonly DateTime Seeded = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static readonly string[] Tables =
        ["therapy_settings", "basal_schedules", "carb_ratio_schedules", "sensitivity_schedules", "target_range_schedules"];

    /// <summary>Each seeded row: its store's key, its table, its tenant.</summary>
    internal List<(Guid Id, string Store, string Table, Guid Tenant)> Rows { get; } = [];

    internal Dictionary<Guid, (DateTime Timestamp, DateTime Updated)> AfterUp { get; } = [];
    internal Dictionary<Guid, (DateTime Timestamp, DateTime Updated)> AfterSecondUp { get; } = [];

    private string _migrator = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("repair_epoch_profiles");
        _migrator = database.MigratorConnectionString;

        await MigrateToAsync(PriorMigration);
        await SeedAsync();
        await MigrateToAsync(null);
        await ReadAsync(AfterUp);
        await MigrateToAsync(PriorMigration);
        await MigrateToAsync(null);
        await ReadAsync(AfterSecondUp);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string? target)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migrator, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task SeedAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await Execute(conn, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{TenantA}', 'tenant-a', 'Tenant A', true, now(), now()),
              ('{TenantB}', 'tenant-b', 'Tenant B', true, now(), now());
            """);

        foreach (var store in RepairEpochProfileVersionsTests.Stores)
            await SeedStoreAsync(conn, store);

        await Execute(conn, "SELECT set_config('app.current_tenant_id', '', false);");
    }

    private async Task SeedStoreAsync(NpgsqlConnection conn, RepairEpochProfileVersionsTests.Store store)
    {
        await Execute(conn, $"SELECT set_config('app.current_tenant_id', '{store.Tenant}', false);");
        var deleted = store.Deleted ? $"'{Seeded:O}'" : "NULL";
        var startDate = store.StartDate is null ? "NULL" : $"'{store.StartDate}'";
        foreach (var table in Tables)
        {
            var id = Guid.CreateVersion7();
            var timestamp = (store.Retimed ?? []).Contains(table) ? Seeded : store.Timestamp;
            var columns = table == "therapy_settings"
                ? "units, dia, carbs_hr, delay, per_gi_values, is_default, is_externally_managed, start_date"
                : "entries_json";
            var values = table == "therapy_settings"
                ? $"'mg/dl', 4, 20, 20, false, false, false, {startDate}"
                : "'[]'::jsonb";
            await Execute(conn, $"""
                INSERT INTO {table}
                  (id, tenant_id, timestamp, profile_name, legacy_id, sys_created_at, sys_updated_at, deleted_at, {columns})
                VALUES ('{id}', '{store.Tenant}', '{timestamp:O}', '{store.Name}', '{store.LegacyId}',
                   '{Seeded:O}', '{Seeded:O}', {deleted}, {values});
                """);
            Rows.Add((id, store.Key, table, store.Tenant));
        }
    }

    private static async Task Execute(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ReadAsync(Dictionary<Guid, (DateTime Timestamp, DateTime Updated)> into)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        foreach (var (id, _, table, tenant) in Rows)
        {
            await Execute(conn, $"SELECT set_config('app.current_tenant_id', '{tenant}', false);");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT timestamp, sys_updated_at FROM {table} WHERE id = '{id}';";
            await using var reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue($"{table} {id} is still there");
            into[id] = (reader.GetDateTime(0).ToUniversalTime(), reader.GetDateTime(1).ToUniversalTime());
        }
    }
}

[Trait("Category", "Integration")]
public class RepairEpochProfileVersionsTests : IClassFixture<RepairEpochProfileVersionsFixture>
{
    private readonly RepairEpochProfileVersionsFixture _fixture;

    public RepairEpochProfileVersionsTests(RepairEpochProfileVersionsFixture fixture) => _fixture = fixture;

    /// <summary>A store's five rows, seeded at <paramref name="Timestamp"/> but for the tables in <paramref name="Retimed"/>.</summary>
    internal sealed record Store(
        string Key, Guid Tenant, string Name, string LegacyId, string? StartDate,
        DateTime Timestamp, bool Deleted = false, string[]? Retimed = null);

    private static readonly Guid TenantA = RepairEpochProfileVersionsFixture.TenantA;
    private static readonly Guid TenantB = RepairEpochProfileVersionsFixture.TenantB;
    private static readonly DateTime Epoch = DateTime.UnixEpoch;
    private static readonly DateTime Seeded = RepairEpochProfileVersionsFixture.Seeded;

    internal static readonly Store[] Stores =
    [
        new("editor", TenantA, "Default", "docA:Default", "2025-10-01T06:00:00.000Z", Epoch),
        new("second store of the document", TenantA, "Night", "docA:Night", "2025-10-01T06:00:00.000Z", Epoch),
        new("zone-less", TenantA, "Loose", "docB:Loose", "2025-10-03T07:30:00", Epoch),
        new("offset", TenantA, "Offset", "docC:Offset", "2025-10-04T10:00:00+10:00", Epoch),
        new("isf edited off the epoch", TenantA, "Edited", "docD:Edited", "2025-10-05T00:00:00.000Z", Epoch,
            Retimed: ["sensitivity_schedules"]),
        new("unparseable", TenantA, "Odd", "docE:Odd", "Wed Oct 01 2025 junk", Epoch),
        new("no start date", TenantA, "Bare", "docF:Bare", null, Epoch),
        new("with mills", TenantA, "Dated", "docG:Dated", "2025-10-06T00:00:00.000Z", new(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
        new("deleted", TenantA, "Gone", "docH:Gone", "2025-10-07T00:00:00.000Z", Epoch, Deleted: true),
        new("profile switch", TenantA, "Default@@@@@1759276800000", "switch:Default@@@@@1759276800000",
            "2025-10-08T00:00:00.000Z", Epoch),
        new("other tenant", TenantB, "Default", "docA:Default", "2025-10-02T00:00:00.000Z", Epoch),
    ];

    private IEnumerable<(Guid Id, string Table)> RowsOf(string key) =>
        _fixture.Rows.Where(r => r.Store == key).Select(r => (r.Id, r.Table));

    private void ShouldBeMovedTo(string key, DateTime start, params string[] except)
    {
        var moved = RowsOf(key).Where(r => !except.Contains(r.Table)).ToList();
        moved.Should().HaveCount(5 - except.Length);
        moved.Should().AllSatisfy(r => _fixture.AfterUp[r.Id].Timestamp.Should().Be(start, r.Table));
        moved.Select(r => _fixture.AfterUp[r.Id].Updated).Distinct().Should().ContainSingle()
            .Which.Should().BeAfter(Seeded, "the store's rows take one write stamp");
    }

    private void ShouldBeUntouched(string key, string? table = null)
    {
        var store = Stores.Single(s => s.Key == key);
        foreach (var (id, t) in RowsOf(key).Where(r => table is null || r.Table == table))
        {
            var seeded = (store.Retimed ?? []).Contains(t) ? Seeded : store.Timestamp;
            _fixture.AfterUp[id].Should().Be((seeded, Seeded), t);
        }
    }

    [Theory]
    [InlineData("editor", "2025-10-01T06:00:00Z")]
    [InlineData("second store of the document", "2025-10-01T06:00:00Z")]
    [InlineData("zone-less", "2025-10-03T07:30:00Z")]
    [InlineData("offset", "2025-10-04T00:00:00Z")]
    [InlineData("other tenant", "2025-10-02T00:00:00Z")]
    public void AStoreAtTheEpoch_MovesToItsStartDate_WithEverySchedule(string key, string start) =>
        ShouldBeMovedTo(key, DateTimeOffset.Parse(start).UtcDateTime);

    [Fact]
    public void AScheduleAlreadyOffTheEpoch_StaysWhereItIs_AndTheRestMove()
    {
        ShouldBeMovedTo("isf edited off the epoch", new DateTime(2025, 10, 5, 0, 0, 0, DateTimeKind.Utc),
            except: "sensitivity_schedules");
        ShouldBeUntouched("isf edited off the epoch", "sensitivity_schedules");
    }

    [Theory]
    [InlineData("unparseable")]
    [InlineData("no start date")]
    [InlineData("with mills")]
    [InlineData("deleted")]
    [InlineData("profile switch")]
    public void AStoreItCannotOrNeedNotMove_IsUntouched(string key) => ShouldBeUntouched(key);

    [Fact]
    public void RunningItAgain_ChangesNothing() =>
        _fixture.AfterSecondUp.Should().BeEquivalentTo(_fixture.AfterUp);
}
