using System.Text.Json;
using System.Text.Json.Nodes;
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
/// Runs <c>RepairRawMmolProfiles</c> over the schedules and activation snapshots of two tenants on a
/// real PostgreSQL, after <c>RepairDoubleConvertedMmolProfiles</c> in the same run, then down and up
/// again, keeping the notices of the first run.
/// </summary>
public sealed class RepairRawMmolProfilesFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261021130540_AddGoogleHealthGrantHolders";

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111112355");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222355");

    internal static readonly DateTime Seeded = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    internal List<(Guid Id, Guid Tenant, string Table, string Json, DateTime Updated)> Rows { get; } = [];

    internal Dictionary<Guid, (string Json, DateTime Updated)> AfterUp { get; } = [];
    internal Dictionary<Guid, (string Json, DateTime Updated)> AfterSecondUp { get; } = [];
    internal List<string> Notices { get; } = [];

    private string _migrator = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("repair_raw_mmol_profiles");
        _migrator = database.MigratorConnectionString;

        await MigrateToAsync(PriorMigration);
        await SeedAsync();
        await MigrateToAsync(null, Notices);
        await ReadAsync(AfterUp);
        await MigrateToAsync(PriorMigration);
        await MigrateToAsync(null);
        await ReadAsync(AfterSecondUp);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string? target, List<string>? notices = null)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        if (notices is not null)
            conn.Notice += (_, e) => notices.Add(e.Notice.MessageText);
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    internal static string Targets(params (double Low, double High)[] entries) => JsonSerializer.Serialize(
        entries.Select((e, i) => new { Time = $"{i * 6:00}:00", Low = e.Low, High = e.High, TimeAsSeconds = i * 21600 }));

    internal static string Values(params double[] values) => JsonSerializer.Serialize(
        values.Select((v, i) => new { Time = $"{i * 6:00}:00", Value = v, TimeAsSeconds = i * 21600 }));

    internal static string Snapshot(string units, string targets, string sensitivity) =>
        $$"""{"Basal":{{Values(0.8)}},"CarbRatio":{{Values(9.1)}},"Sensitivity":{{sensitivity}},"Targets":{{targets}},"Units":"{{units}}","Dia":5}""";

    private async Task SeedAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await Execute(conn, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{TenantA}', 'tenant-a', 'Tenant A', true, now(), now()),
              ('{TenantB}', 'tenant-b', 'Tenant B', true, now(), now());
            """);

        foreach (var store in RepairRawMmolProfilesTests.Stores)
            await SeedStoreAsync(conn, store);
        foreach (var activation in RepairRawMmolProfilesTests.Activations)
        {
            await Context(conn, activation.Tenant);
            var deleted = activation.Deleted ? $"'{Seeded:O}'" : "NULL";
            await Execute(conn, $"""
                INSERT INTO profile_activations
                  (id, tenant_id, start_timestamp, phase, origin, base_profile_name, percentage, timeshift_ms,
                   snapshot, snapshot_is_customized, sys_created_at, sys_updated_at, deleted_at)
                VALUES ('{activation.Id}', '{activation.Tenant}', '{Seeded:O}', 'Requested', 'Uploader', 'Switch', 100, 0,
                   '{activation.Snapshot}'::jsonb, false, '{Seeded:O}', '{Seeded:O}', {deleted});
                """);
            Rows.Add((activation.Id, activation.Tenant, "profile_activations", activation.Snapshot, Seeded));
        }

        await Execute(conn, "SELECT set_config('app.current_tenant_id', '', false);");
    }

    private async Task SeedStoreAsync(NpgsqlConnection conn, RepairRawMmolProfilesTests.Store store)
    {
        await Context(conn, store.Tenant);
        string Key(string? value) => value is null ? "NULL" : $"'{value}'";
        var correlation = store.Correlation is { } c ? $"'{c}'" : "NULL";
        var deleted = store.Deleted ? $"'{Seeded:O}'" : "NULL";
        var targetUpdated = store.TargetUpdated ?? Seeded;
        var isfUpdated = store.IsfUpdated ?? Seeded;
        var timestamp = store.Timestamp ?? Seeded;
        var isfTimestamp = store.IsfTimestamp ?? timestamp;
        if (store.Owner)
        {
            await Execute(conn, $"""
                INSERT INTO therapy_settings
                  (id, tenant_id, timestamp, profile_name, units, dia, carbs_hr, delay, per_gi_values, is_default,
                   is_externally_managed, legacy_id, correlation_id, sys_created_at, sys_updated_at, deleted_at)
                VALUES ('{Guid.CreateVersion7()}', '{store.Tenant}', '{timestamp:O}', '{store.Name}', '{store.Units}', 5, 20, 20,
                   false, false, false, {Key(store.LegacyId)}, {correlation}, '{Seeded:O}', '{Seeded:O}', {deleted});
                """);
        }
        if (store.Targets is not null)
        {
            await Execute(conn, $"""
                INSERT INTO target_range_schedules
                  (id, tenant_id, timestamp, profile_name, entries_json, legacy_id, correlation_id, sys_created_at, sys_updated_at, deleted_at)
                VALUES ('{store.TargetId}', '{store.Tenant}', '{store.TargetTimestamp ?? timestamp:O}', '{store.Name}', '{store.Targets}'::jsonb,
                   {Key(store.LegacyId)}, {correlation}, '{Seeded:O}', '{targetUpdated:O}', {deleted});
                """);
            Rows.Add((store.TargetId, store.Tenant, "target_range_schedules", store.Targets, targetUpdated));
        }
        if (store.Sensitivity is null)
            return;
        await Execute(conn, $"""
            INSERT INTO sensitivity_schedules
              (id, tenant_id, timestamp, profile_name, entries_json, legacy_id, correlation_id, sys_created_at, sys_updated_at, deleted_at)
            VALUES ('{store.SensitivityId}', '{store.Tenant}', '{isfTimestamp:O}', '{store.Name}', '{store.Sensitivity}'::jsonb,
               {Key(store.LegacyId)}, {correlation}, '{Seeded:O}', '{isfUpdated:O}', {deleted});
            """);
        Rows.Add((store.SensitivityId, store.Tenant, "sensitivity_schedules", store.Sensitivity, isfUpdated));
    }

    private static Task Context(NpgsqlConnection conn, Guid tenant) =>
        Execute(conn, $"SELECT set_config('app.current_tenant_id', '{tenant}', false);");

    private static async Task Execute(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ReadAsync(Dictionary<Guid, (string Json, DateTime Updated)> into)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        foreach (var (id, tenant, table, _, _) in Rows)
        {
            await Context(conn, tenant);
            await using var cmd = conn.CreateCommand();
            var column = table == "profile_activations" ? "snapshot" : "entries_json";
            cmd.CommandText = $"SELECT {column}::text, sys_updated_at FROM {table} WHERE id = '{id}';";
            await using var reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue($"{table} {id} is still there");
            into[id] = (reader.GetString(0), reader.GetDateTime(1));
        }
    }
}

[Trait("Category", "Integration")]
public class RepairRawMmolProfilesTests : IClassFixture<RepairRawMmolProfilesFixture>
{
    private readonly RepairRawMmolProfilesFixture _fixture;

    public RepairRawMmolProfilesTests(RepairRawMmolProfilesFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A target schedule and an ISF schedule under the same keys, each unless null, with a therapy settings
    /// row as their owner when <paramref name="Owner"/>.
    /// </summary>
    internal sealed record Store(
        string Key, Guid Tenant, string Name, string Units, string? LegacyId, Guid? Correlation,
        string? Targets, string? Sensitivity, DateTime? IsfUpdated = null, bool Deleted = false,
        DateTime? IsfTimestamp = null, DateTime? TargetUpdated = null, bool Owner = true, DateTime? Timestamp = null,
        DateTime? TargetTimestamp = null)
    {
        public Guid TargetId { get; } = Guid.CreateVersion7();
        public Guid SensitivityId { get; } = Guid.CreateVersion7();
    }

    internal sealed record Activation(string Key, Guid Id, Guid Tenant, string Snapshot, bool Deleted = false);

    private static readonly Guid TenantA = RepairRawMmolProfilesFixture.TenantA;
    private static readonly Guid TenantB = RepairRawMmolProfilesFixture.TenantB;
    private static readonly DateTime Seeded = RepairRawMmolProfilesFixture.Seeded;

    private static string T(params (double, double)[] e) => RepairRawMmolProfilesFixture.Targets(e);
    private static string V(params double[] v) => RepairRawMmolProfilesFixture.Values(v);

    // A mmol profile of targets 5.0/5.5 and 6.0/7.0 and ISF 3.5 and 3.2, as stored before #521 and as since.
    private static readonly string RawTargets = T((5.0, 5.5), (6.0, 7.0));
    private static readonly string RawIsf = V(3.5, 3.2);
    private static readonly string CorrectTargets = T((90, 99), (108, 126));
    private static readonly string CorrectIsf = V(63, 58);

    // The same store after one more conversion, which RepairDoubleConvertedMmolProfiles undoes.
    private static readonly string OnceTargets = T((1622, 1784), (1946, 2270));
    private static readonly string OnceIsf = V(1135, 1045);

    private static readonly Guid Shared = Guid.CreateVersion7();
    private static readonly Guid Tainted = Guid.CreateVersion7();
    private static readonly Guid Repeated = Guid.CreateVersion7();

    internal static readonly Store[] Stores =
    [
        new("raw", TenantA, "AAPS", "mmol", "docA:AAPS", Guid.CreateVersion7(), RawTargets, RawIsf),
        new("raw by correlation", TenantA, "Loop", "mmol/L", null, Guid.CreateVersion7(), RawTargets, RawIsf),
        new("other tenant, raw", TenantB, "Night", "mmol", "docB:Night", null, RawTargets, RawIsf),
        new("isf 59 s after its targets", TenantA, "Within", "mmol", "docA:Within", null, RawTargets, RawIsf,
            IsfUpdated: Seeded.AddSeconds(59)),
        new("isf 12", TenantA, "Isf12", "mmol", "docA:Isf12", null, RawTargets, V(12)),
        new("isf 3.5 beside 12", TenantA, "Mixed", "mmol", "docA:Mixed", null, RawTargets, V(3.5, 12)),
        new("isf 0.5", TenantA, "Isf05", "mmol", "docA:Isf05", null, RawTargets, V(0.5)),
        new("isf 22.2", TenantA, "Isf222", "mmol", "docA:Isf222", null, RawTargets, V(22.2)),
        new("targets 2.0 and 22.2", TenantA, "Edges", "mmol", "docA:Edges", null, T((2.0, 22.2)), RawIsf),

        new("isf 30", TenantA, "Isf30", "mmol", "docA:Isf30", null, RawTargets, V(30)),
        new("isf 22.3", TenantA, "Isf223", "mmol", "docA:Isf223", null, RawTargets, V(22.3)),
        new("isf 0.4", TenantA, "Isf04", "mmol", "docA:Isf04", null, RawTargets, V(0.4)),
        new("isf already converted", TenantA, "Isf63", "mmol", "docA:Isf63", null, RawTargets, V(63)),
        new("isf as a string", TenantA, "IsfText", "mmol", "docA:IsfText", null, RawTargets,
            """[{"Time":"00:00","Value":"3.5","TimeAsSeconds":0}]"""),
        new("isf empty", TenantA, "IsfEmpty", "mmol", "docA:IsfEmpty", null, RawTargets, "[]"),
        new("isf not an array", TenantA, "IsfObject", "mmol", "docA:IsfObject", null, RawTargets, """{"Value":3.5}"""),
        new("isf entries not objects", TenantA, "IsfScalars", "mmol", "docA:IsfScalars", null, RawTargets, "[3.5, 3.2]"),
        new("isf 61 s after its targets", TenantA, "Outside", "mmol", "docA:Outside", null, RawTargets, RawIsf,
            IsfUpdated: Seeded.AddSeconds(61)),
        new("isf from another batch", TenantA, "Later", "mmol", "docA:Later", null, RawTargets, RawIsf,
            IsfTimestamp: Seeded.AddHours(1)),
        new("no isf", TenantA, "NoIsf", "mmol", "docA:NoIsf", null, RawTargets, null),

        new("correct mmol", TenantA, "Fine", "mmol", "docA:Fine", null, CorrectTargets, CorrectIsf),
        new("mg/dL", TenantA, "Mgdl", "mg/dl", "docA:Mgdl", null, RawTargets, RawIsf),
        new("deleted", TenantA, "Gone", "mmol", "docA:Gone", null, RawTargets, RawIsf, Deleted: true),
        new("other tenant, same key, correct", TenantB, "AAPS", "mmol", "docA:AAPS", null, CorrectTargets, CorrectIsf),
        new("target 1.9", TenantA, "Low19", "mmol", "docA:Low19", null, T((1.9, 5.5), (6.0, 7.0)), RawIsf),
        new("target 22.3", TenantA, "High223", "mmol", "docA:High223", null, T((5.0, 22.3)), RawIsf),
        new("target as a string", TenantA, "TargetText", "mmol", "docA:TargetText", null,
            """[{"Time":"00:00","Low":"5.0","High":5.5,"TimeAsSeconds":0}]""", RawIsf),
        new("targets not an array", TenantA, "TargetObject", "mmol", "docA:TargetObject", null,
            """{"Low":5.0,"High":5.5}""", RawIsf),
        new("target entries not objects", TenantA, "TargetScalars", "mmol", "docA:TargetScalars", null, "[5.0, 5.5]", RawIsf),

        new("shared isf, by legacy id", TenantA, "Shared", "mmol", "docA:Shared", Shared, RawTargets, RawIsf),
        new("shared isf, by correlation", TenantA, "Shared", "mmol", null, Shared, RawTargets, null, Owner: false),
        new("tainted isf, by legacy id", TenantA, "Tainted", "mmol", "docA:Tainted", Tainted, RawTargets, RawIsf),
        new("tainted isf, by correlation, another batch", TenantA, "Tainted", "mmol", null, Tainted, RawTargets, null,
            TargetUpdated: Seeded.AddMinutes(2), Owner: false),
        new("raw beside repeat-converted, raw row", TenantA, "Repeated", "mmol", "docA:Repeated", Repeated, RawTargets, RawIsf),
        new("raw beside repeat-converted, converted row", TenantA, "Repeated", "mmol", null, Repeated, OnceTargets, OnceIsf,
            Owner: false),

        new("older store beside a newer orphan raw isf", TenantA, "Orphaned", "mmol", "d1:Orphaned", null, RawTargets, RawIsf),
        new("newer orphan raw isf", TenantA, "Orphaned", "mmol", "d2:Orphaned", null, null, V(3.0),
            Owner: false, Timestamp: Seeded.AddDays(1)),
        new("older raw store", TenantA, "Superseded", "mmol", "d1:Superseded", null, RawTargets, RawIsf),
        new("newer raw store", TenantA, "Superseded", "mmol", "d2:Superseded", null, RawTargets, RawIsf,
            Timestamp: Seeded.AddDays(1)),
        new("store beside an older orphan raw isf", TenantA, "Fallback", "mmol", "d1:Fallback", null, RawTargets, RawIsf),
        new("older orphan raw isf", TenantA, "Fallback", "mmol", "d0:Fallback", null, null, V(3.0),
            Owner: false, Timestamp: Seeded.AddDays(-1)),
        new("raw store beside a newer converted isf", TenantA, "Corrected", "mmol", "d1:Corrected", null, RawTargets, RawIsf),
        new("newer converted mmol store with isf 15", TenantA, "Corrected", "mmol", "d2:Corrected", null, CorrectTargets, V(15),
            Timestamp: Seeded.AddDays(1)),
        new("raw store beside a store whose targets were retimed", TenantA, "Retimed", "mmol", "d1:Retimed", null, RawTargets, RawIsf),
        new("store with targets edited later beside its raw isf", TenantA, "Retimed", "mmol", "d2:Retimed", null, T((100, 110)), V(3.0),
            Timestamp: Seeded.AddDays(1), TargetTimestamp: Seeded.AddDays(2)),
        new("mmol store with mg/dL targets and isf 5", TenantA, "Under9", "mmol", "docA:Under9", null, CorrectTargets, V(5)),
        new("raw store beside a newer mg/dL isf", TenantA, "Switched", "mmol", "d1:Switched", null, RawTargets, RawIsf),
        new("newer mg/dL store with isf 15", TenantA, "Switched", "mg/dl", "d2:Switched", null, CorrectTargets, V(15),
            Timestamp: Seeded.AddDays(1)),
    ];

    internal static readonly Activation[] Activations =
    [
        new("snapshot raw", Guid.CreateVersion7(), TenantA, RepairRawMmolProfilesFixture.Snapshot("mmol", RawTargets, RawIsf)),
        new("snapshot isf 12", Guid.CreateVersion7(), TenantA, RepairRawMmolProfilesFixture.Snapshot("mmol", RawTargets, V(12))),
        new("snapshot isf 30", Guid.CreateVersion7(), TenantA, RepairRawMmolProfilesFixture.Snapshot("mmol", RawTargets, V(30))),
        new("snapshot no isf", Guid.CreateVersion7(), TenantA, RepairRawMmolProfilesFixture.Snapshot("mmol", RawTargets, "[]")),
        new("snapshot correct", Guid.CreateVersion7(), TenantA,
            RepairRawMmolProfilesFixture.Snapshot("mmol", CorrectTargets, CorrectIsf)),
        new("snapshot mg/dL", Guid.CreateVersion7(), TenantB, RepairRawMmolProfilesFixture.Snapshot("mg/dl", RawTargets, RawIsf)),
        new("snapshot deleted", Guid.CreateVersion7(), TenantB,
            RepairRawMmolProfilesFixture.Snapshot("mmol", RawTargets, RawIsf), Deleted: true),
    ];

    private static Store StoreOf(string key) => Stores.Single(s => s.Key == key);

    private static Activation ActivationOf(string key) => Activations.Single(a => a.Key == key);

    private static double[] Numbers(string json, string key) =>
        JsonNode.Parse(json)!.AsArray().Select(e => e![key]!.GetValue<double>()).ToArray();

    private (double[] Low, double[] High) TargetsAfter(Store store)
    {
        var targets = _fixture.AfterUp[store.TargetId].Json;
        return (Numbers(targets, "Low"), Numbers(targets, "High"));
    }

    private double[] IsfAfter(Store store) => Numbers(_fixture.AfterUp[store.SensitivityId].Json, "Value");

    private void ShouldHaveConvertedTargets(Store store)
    {
        var (low, high) = TargetsAfter(store);
        low.Should().Equal(90, 108);
        high.Should().Equal(99, 126);
        _fixture.AfterUp[store.TargetId].Updated.Should().BeAfter(Seeded);
    }

    private void ShouldBeUntouched(Guid id)
    {
        var seeded = _fixture.Rows.Single(r => r.Id == id);
        JsonNode.DeepEquals(JsonNode.Parse(_fixture.AfterUp[id].Json), JsonNode.Parse(seeded.Json)).Should().BeTrue();
        _fixture.AfterUp[id].Updated.Should().Be(seeded.Updated);
    }

    private void ShouldBeLeftWhole(Store store)
    {
        if (store.Targets is not null)
            ShouldBeUntouched(store.TargetId);
        if (store.Sensitivity is not null)
            ShouldBeUntouched(store.SensitivityId);
    }

    private void ShouldBeReportedLeft(Guid id) =>
        _fixture.Notices.Should().Contain(n => n.Contains("left in mmol") && n.Contains(id.ToString()));

    [Theory]
    [InlineData("raw")]
    [InlineData("raw by correlation")]
    [InlineData("other tenant, raw")]
    [InlineData("isf 59 s after its targets")]
    [InlineData("older raw store")]
    [InlineData("newer raw store")]
    [InlineData("raw store beside a newer mg/dL isf")]
    [InlineData("raw store beside a newer converted isf")]
    public void ARawStore_HasItsTargetsAndIsfConverted(string key)
    {
        var store = StoreOf(key);
        ShouldHaveConvertedTargets(store);
        IsfAfter(store).Should().Equal(63, 58);
        _fixture.AfterUp[store.SensitivityId].Updated.Should().BeAfter(Seeded);
    }

    [Theory]
    [InlineData("isf 12", new double[] { 216 })]
    [InlineData("isf 3.5 beside 12", new double[] { 63, 216 })]
    [InlineData("isf 0.5", new double[] { 9 })]
    [InlineData("isf 22.2", new double[] { 400 })]
    public void AnIsfAnywhereInTheBand_IsConvertedWithItsTargets(string key, double[] mgdl)
    {
        var store = StoreOf(key);
        ShouldHaveConvertedTargets(store);
        IsfAfter(store).Should().Equal(mgdl);
    }

    [Fact]
    public void TargetsAtTheEdgesOfTheBand_AreConverted()
    {
        var (low, high) = TargetsAfter(StoreOf("targets 2.0 and 22.2"));
        low.Should().Equal(36);
        high.Should().Equal(400);
    }

    [Fact]
    public void AConversionKeepsEveryOtherFieldOfEachEntry()
    {
        var entry = JsonNode.Parse(_fixture.AfterUp[StoreOf("raw").TargetId].Json)!.AsArray()[1]!;
        entry["Time"]!.GetValue<string>().Should().Be("06:00");
        entry["TimeAsSeconds"]!.GetValue<int>().Should().Be(21600);
    }

    /// <summary>
    /// Raw targets make AAPS reject the profile; converting them beside an ISF left raw would let that ISF
    /// through 18 times too strong, so the whole store is left.
    /// </summary>
    [Theory]
    [InlineData("isf 30")]
    [InlineData("isf 22.3")]
    [InlineData("isf 0.4")]
    [InlineData("isf already converted")]
    [InlineData("isf as a string")]
    [InlineData("isf empty")]
    [InlineData("isf not an array")]
    [InlineData("isf entries not objects")]
    [InlineData("isf 61 s after its targets")]
    [InlineData("isf from another batch")]
    [InlineData("no isf")]
    public void AStoreWithAnIsfItCannotConvert_IsLeftWholeAndReported(string key)
    {
        var store = StoreOf(key);
        ShouldBeLeftWhole(store);
        ShouldBeReportedLeft(store.TargetId);
    }

    [Theory]
    [InlineData("correct mmol")]
    [InlineData("mg/dL")]
    [InlineData("deleted")]
    [InlineData("other tenant, same key, correct")]
    [InlineData("target 1.9")]
    [InlineData("target 22.3")]
    [InlineData("target as a string")]
    [InlineData("targets not an array")]
    [InlineData("target entries not objects")]
    public void AStoreWhoseTargetsAreNotAllRawMmol_IsUntouched(string key) => ShouldBeLeftWhole(StoreOf(key));

    [Fact]
    public void AStoreLeftWithATargetUnderTheBound_IsReported() =>
        _fixture.Notices.Should().Contain(n =>
            n.Contains("still holding a target at or under 22.2") && n.Contains(StoreOf("target 1.9").TargetId.ToString()));

    [Fact]
    public void TwoRawTargetSchedulesOfOneBatch_AreConvertedWithTheirSharedIsfOnce()
    {
        ShouldHaveConvertedTargets(StoreOf("shared isf, by legacy id"));
        ShouldHaveConvertedTargets(StoreOf("shared isf, by correlation"));
        IsfAfter(StoreOf("shared isf, by legacy id")).Should().Equal(63, 58);
    }

    [Fact]
    public void AnIsfSharedWithATargetScheduleOfAnotherBatch_LeavesBothTargetSchedules()
    {
        ShouldBeLeftWhole(StoreOf("tainted isf, by legacy id"));
        ShouldBeLeftWhole(StoreOf("tainted isf, by correlation, another batch"));
        ShouldBeReportedLeft(StoreOf("tainted isf, by legacy id").TargetId);
    }

    /// <summary>
    /// RepairDoubleConvertedMmolProfiles converts the repeat-converted row back first, which moves its ISF's
    /// sys_updated_at, so the raw row of the same store no longer has every ISF in its batch.
    /// </summary>
    [Fact]
    public void ARawRowBesideARepeatConvertedRowOfItsStore_IsLeftWhileThatRowIsUndone()
    {
        var converted = StoreOf("raw beside repeat-converted, converted row");
        ShouldHaveConvertedTargets(converted);
        IsfAfter(converted).Should().Equal(63, 58);
        ShouldBeLeftWhole(StoreOf("raw beside repeat-converted, raw row"));
        ShouldBeReportedLeft(StoreOf("raw beside repeat-converted, raw row").TargetId);
    }

    /// <summary>
    /// The resolvers read a profile name's newest targets beside its newest ISF, whatever document each came
    /// from, so the older store's converted targets would be read beside the orphan's ISF of 3.0 mg/dL/U.
    /// </summary>
    [Fact]
    public void AStoreBesideANewerRawIsfOfItsProfileNameThatStaysRaw_IsLeftWholeAndBothAreReported()
    {
        var older = StoreOf("older store beside a newer orphan raw isf");
        var orphan = StoreOf("newer orphan raw isf");
        ShouldBeLeftWhole(older);
        ShouldBeUntouched(orphan.SensitivityId);
        ShouldBeReportedLeft(older.TargetId);
        _fixture.Notices.Should().Contain(n =>
            n.Contains("still holding an ISF at 0.5 to 22.2") && n.Contains(orphan.SensitivityId.ToString()));
    }

    /// <summary>
    /// A time before every version resolves to the newest one, so the store's converted targets would be
    /// read before it beside the older orphan's ISF of 3.0 mg/dL/U.
    /// </summary>
    [Fact]
    public void AStoreBesideAnOlderRawIsfOfItsProfileNameThatStaysRaw_IsLeftWhole()
    {
        ShouldBeLeftWhole(StoreOf("store beside an older orphan raw isf"));
        ShouldBeUntouched(StoreOf("older orphan raw isf").SensitivityId);
        ShouldBeReportedLeft(StoreOf("store beside an older orphan raw isf").TargetId);
    }

    /// <summary>
    /// A V4 edit retimed and corrected the newer store's targets, so between its ISF's timestamp and theirs
    /// the resolvers would read the older store's converted targets beside that ISF of 3.0 mg/dL/U.
    /// </summary>
    [Fact]
    public void AStoreBesideAnIsfWhoseTargetsWereRetimed_IsLeftWhole_AndThatIsfIsReported()
    {
        var retimed = StoreOf("store with targets edited later beside its raw isf");
        ShouldBeLeftWhole(StoreOf("raw store beside a store whose targets were retimed"));
        ShouldBeLeftWhole(retimed);
        _fixture.Notices.Should().Contain(n =>
            n.Contains("still holding an ISF at 0.5 to 22.2") && n.Contains(retimed.SensitivityId.ToString()));
    }

    [Fact]
    public void AMmolIsfUnder9_IsReportedWhateverItsTargets()
    {
        var store = StoreOf("mmol store with mg/dL targets and isf 5");
        ShouldBeLeftWhole(store);
        _fixture.Notices.Should().Contain(n => n.Contains("ISF under 9 mg/dL/U") && n.Contains(store.SensitivityId.ToString()));
    }

    [Fact]
    public void ACorrectIsfOfTheProfileName_IsNeitherABlockerNorReported()
    {
        var correct = StoreOf("newer converted mmol store with isf 15");
        ShouldBeUntouched(correct.SensitivityId);
        _fixture.Notices.Should().NotContain(n => n.Contains(correct.SensitivityId.ToString()));
    }

    [Fact]
    public void ACorrectMmolIsfOf9To22_IsNotReportedAsLeftOver()
    {
        var store = StoreOf("isf 0.5");
        _fixture.Notices.Should().NotContain(n =>
            n.Contains("still holding an ISF") && n.Contains(store.SensitivityId.ToString()));
    }

    [Theory]
    [InlineData("snapshot raw", new double[] { 63, 58 })]
    [InlineData("snapshot isf 12", new double[] { 216 })]
    public void ARawMmolActivationSnapshot_HasItsTargetsAndIsfConverted_AndKeepsTheRest(string key, double[] isf)
    {
        var snapshot = JsonNode.Parse(_fixture.AfterUp[ActivationOf(key).Id].Json)!;
        Numbers(snapshot["Targets"]!.ToJsonString(), "Low").Should().Equal(90, 108);
        Numbers(snapshot["Targets"]!.ToJsonString(), "High").Should().Equal(99, 126);
        Numbers(snapshot["Sensitivity"]!.ToJsonString(), "Value").Should().Equal(isf);
        Numbers(snapshot["Basal"]!.ToJsonString(), "Value").Should().Equal(0.8);
        Numbers(snapshot["CarbRatio"]!.ToJsonString(), "Value").Should().Equal(9.1);
        snapshot["Units"]!.GetValue<string>().Should().Be("mmol");
    }

    [Theory]
    [InlineData("snapshot isf 30")]
    [InlineData("snapshot no isf")]
    public void ARawActivationSnapshotWithAnIsfItCannotConvert_IsLeftWholeAndReported(string key)
    {
        ShouldBeUntouched(ActivationOf(key).Id);
        ShouldBeReportedLeft(ActivationOf(key).Id);
    }

    [Theory]
    [InlineData("snapshot correct")]
    [InlineData("snapshot mg/dL")]
    [InlineData("snapshot deleted")]
    public void AnActivationSnapshotTheRepairDoesNotSelect_IsUntouched(string key) =>
        ShouldBeUntouched(ActivationOf(key).Id);

    [Fact]
    public void RunningAgain_ChangesNothing()
    {
        foreach (var (id, _, _, _, _) in _fixture.Rows)
        {
            JsonNode.DeepEquals(JsonNode.Parse(_fixture.AfterSecondUp[id].Json), JsonNode.Parse(_fixture.AfterUp[id].Json))
                .Should().BeTrue();
            _fixture.AfterSecondUp[id].Updated.Should().Be(_fixture.AfterUp[id].Updated);
        }
    }
}

