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
    internal static readonly Guid TenantC = Guid.Parse("c3333333-3333-7333-8333-333333332377");
    internal static readonly Guid TenantD = Guid.Parse("d4444444-4444-7444-8444-444444442377");
    internal static readonly Guid TenantE = Guid.Parse("e5555555-5555-7555-8555-555555552377");

    internal static readonly DateTime Seeded = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static readonly string[] Tables =
        ["therapy_settings", "basal_schedules", "carb_ratio_schedules", "sensitivity_schedules", "target_range_schedules"];

    /// <summary>Each seeded row: its store's key, its table, its tenant.</summary>
    internal List<(Guid Id, string Store, string Table, Guid Tenant)> Rows { get; } = [];

    internal Dictionary<Guid, (DateTime Timestamp, DateTime Updated)> AfterUp { get; } = [];
    internal Dictionary<Guid, (DateTime Timestamp, DateTime Updated)> AfterSecondUp { get; } = [];
    internal Dictionary<Guid, bool> DefaultAfterUp { get; } = [];
    internal List<(Guid Tenant, string Job)> JobsAfterUp { get; } = [];

    private string _migrator = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "repair_epoch_profiles");
        _migrator = database.MigratorConnectionString;

        await SeedAsync();
        await MigrateToAsync(null);
        await ReadAsync(AfterUp);
        await ReadDefaultsAndJobsAsync();
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
              ('{TenantB}', 'tenant-b', 'Tenant B', true, now(), now()),
              ('{TenantC}', 'tenant-c', 'Tenant C', false, now(), now()),
              ('{TenantD}', 'tenant-d', 'Tenant D', true, now(), now()),
              ('{TenantE}', 'tenant-e', 'Tenant E', true, now(), now());
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
        var documentDefault = store.DocumentDefault is null ? "NULL" : $"'{store.DocumentDefault}'";
        foreach (var table in Tables)
        {
            var id = Guid.CreateVersion7();
            var timestamp = (store.Retimed ?? []).Contains(table) ? Seeded : store.Timestamp;
            var columns = table == "therapy_settings"
                ? "units, dia, carbs_hr, delay, per_gi_values, is_default, is_externally_managed, start_date, document_default_profile"
                : "entries_json";
            var values = table == "therapy_settings"
                ? $"'mg/dl', 4, 20, 20, false, {(store.IsDefault ? "true" : "false")}, false, {startDate}, {documentDefault}"
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

    private async Task ReadDefaultsAndJobsAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        foreach (var (id, _, _, tenant) in Rows.Where(r => r.Table == "therapy_settings"))
        {
            await Execute(conn, $"SELECT set_config('app.current_tenant_id', '{tenant}', false);");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT is_default FROM therapy_settings WHERE id = '{id}';";
            DefaultAfterUp[id] = (bool)(await cmd.ExecuteScalarAsync())!;
        }

        await using var jobs = conn.CreateCommand();
        // The chain runs on past AddJobLedger, which moves the migration's pending_tenant_jobs rows there.
        jobs.CommandText = "SELECT tenant_id, job FROM job_ledger WHERE next_due_at IS NOT NULL;";
        await using var reader = await jobs.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            JobsAfterUp.Add((reader.GetGuid(0), reader.GetString(1)));
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
        DateTime Timestamp, bool Deleted = false, string[]? Retimed = null,
        bool IsDefault = false, string? DocumentDefault = null);

    private static readonly Guid TenantA = RepairEpochProfileVersionsFixture.TenantA;
    private static readonly Guid TenantB = RepairEpochProfileVersionsFixture.TenantB;
    private static readonly Guid TenantC = RepairEpochProfileVersionsFixture.TenantC;
    private static readonly Guid TenantD = RepairEpochProfileVersionsFixture.TenantD;
    private static readonly Guid TenantE = RepairEpochProfileVersionsFixture.TenantE;
    private static readonly DateTime September = new(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc);
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
        new("unparseable", TenantA, "Odd", "docE:Odd", "Wed Oct 01 2025 06:00:00 GMT+0000", Epoch),
        new("now", TenantA, "Now", "docE2:Now", "now", Epoch),
        new("infinity", TenantA, "Infinite", "docE3:Infinite", "infinity", Epoch),
        new("out of range", TenantA, "Month13", "docE4:Month13", "2025-13-01T00:00:00Z", Epoch),
        new("the epoch", TenantA, "Epoch", "docE5:Epoch", "1970-01-01T00:00:00.000Z", Epoch),
        new("no start date", TenantA, "Bare", "docF:Bare", null, Epoch),
        new("with mills", TenantA, "Dated", "docG:Dated", "2025-10-06T00:00:00.000Z", September),
        new("deleted", TenantA, "Gone", "docH:Gone", "2025-10-07T00:00:00.000Z", Epoch, Deleted: true),
        new("profile switch", TenantA, "Default@@@@@1759276800000", "switch:Default@@@@@1759276800000",
            "2025-10-08T00:00:00.000Z", Epoch),
        new("other tenant", TenantB, "Default", "docA:Default", "2025-10-02T00:00:00.000Z", Epoch),
        new("older document holding the default", TenantC, "Default", "old:Default", "2025-09-01T00:00:00.000Z", September,
            IsDefault: true, DocumentDefault: "Default"),
        new("newest document's default store", TenantC, "Night", "new:Night", "2025-10-10T00:00:00.000Z", Epoch,
            DocumentDefault: "Night"),
        new("newest document's other store", TenantC, "Day", "new:Day", "2025-10-10T00:00:00.000Z", Epoch,
            DocumentDefault: "Night"),
        new("older document's store a user made the default", TenantE, "Exercise", "older:Exercise",
            "2025-09-01T00:00:00.000Z", September, IsDefault: true, DocumentDefault: "Default"),
        new("older document's named default", TenantE, "Default", "older:Default",
            "2025-09-01T00:00:00.000Z", September, DocumentDefault: "Default"),
        new("newest document's store of the user's name", TenantE, "Exercise", "newer:Exercise",
            "2025-10-10T00:00:00.000Z", Epoch, DocumentDefault: "Default"),
        new("newest document's named default", TenantE, "Default", "newer:Default",
            "2025-10-10T00:00:00.000Z", Epoch, DocumentDefault: "Default"),
        new("tenant with nothing at the epoch", TenantD, "Default", "docA:Default", "2025-10-06T00:00:00.000Z", September,
            IsDefault: true, DocumentDefault: "Default"),
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
    [InlineData("newest document's default store", "2025-10-10T00:00:00Z")]
    [InlineData("newest document's other store", "2025-10-10T00:00:00Z")]
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
    [InlineData("now")]
    [InlineData("infinity")]
    [InlineData("out of range")]
    [InlineData("the epoch")]
    [InlineData("tenant with nothing at the epoch")]
    [InlineData("no start date")]
    [InlineData("with mills")]
    [InlineData("deleted")]
    [InlineData("profile switch")]
    public void AStoreItCannotOrNeedNotMove_IsUntouched(string key) => ShouldBeUntouched(key);

    private Guid SettingsOf(string key) => RowsOf(key).Single(r => r.Table == "therapy_settings").Id;

    [Fact]
    public void TheDefault_MovesToTheStoreTheNewestDocumentNames()
    {
        _fixture.DefaultAfterUp[SettingsOf("newest document's default store")].Should().BeTrue();
        _fixture.DefaultAfterUp[SettingsOf("newest document's other store")].Should().BeFalse();
        _fixture.DefaultAfterUp[SettingsOf("older document holding the default")].Should().BeFalse();
        _fixture.AfterUp[SettingsOf("older document holding the default")].Timestamp.Should().Be(September);
    }

    [Fact]
    public void ADefaultAUserChose_MovesToTheNewestDocumentsStoreOfThatName()
    {
        _fixture.DefaultAfterUp[SettingsOf("newest document's store of the user's name")].Should().BeTrue();
        _fixture.DefaultAfterUp[SettingsOf("newest document's named default")].Should().BeFalse();
        _fixture.DefaultAfterUp[SettingsOf("older document's store a user made the default")].Should().BeFalse();
        _fixture.DefaultAfterUp[SettingsOf("older document's named default")].Should().BeFalse();
    }

    [Fact]
    public void ADefaultInATenantItMovedNothingFor_StaysWhereItIs() =>
        _fixture.DefaultAfterUp[SettingsOf("tenant with nothing at the epoch")].Should().BeTrue();

    [Fact]
    public void EveryTenantItMovedRowsFor_InactiveOrNot_GetsOneJob_AndNoOtherDoes() =>
        _fixture.JobsAfterUp.Should().BeEquivalentTo(new[]
        {
            (TenantA, "profile-versions-moved"),
            (TenantB, "profile-versions-moved"),
            (TenantC, "profile-versions-moved"),
            (TenantE, "profile-versions-moved"),
        });

    [Fact]
    public void RunningItAgain_ChangesNothing() =>
        _fixture.AfterSecondUp.Should().BeEquivalentTo(_fixture.AfterUp);
}
