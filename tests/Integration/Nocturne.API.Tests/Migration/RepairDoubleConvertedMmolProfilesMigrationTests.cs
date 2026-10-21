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
/// Runs <c>RepairDoubleConvertedMmolProfiles</c> over the schedules and activation snapshots of two
/// tenants on a real PostgreSQL, then down and up again.
/// </summary>
public sealed class RepairDoubleConvertedMmolProfilesFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261021130540_AddGoogleHealthGrantHolders";

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111111836");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222221836");

    internal static readonly DateTime Seeded = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Each row's tenant, table, key into its JSON, and the JSON as seeded.</summary>
    internal List<(Guid Id, Guid Tenant, string Table, string Json, DateTime Updated)> Rows { get; } = [];

    internal Dictionary<Guid, (string Json, DateTime Updated)> AfterUp { get; } = [];
    internal Dictionary<Guid, (string Json, DateTime Updated)> AfterSecondUp { get; } = [];

    private string _migrator = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("repair_mmol_profiles");
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

        foreach (var store in RepairDoubleConvertedMmolProfilesTests.Stores)
            await SeedStoreAsync(conn, store);
        foreach (var activation in RepairDoubleConvertedMmolProfilesTests.Activations)
        {
            await Context(conn, activation.Tenant);
            await Execute(conn, $"""
                INSERT INTO profile_activations
                  (id, tenant_id, start_timestamp, phase, origin, base_profile_name, percentage, timeshift_ms,
                   snapshot, snapshot_is_customized, sys_created_at, sys_updated_at)
                VALUES ('{activation.Id}', '{activation.Tenant}', '{Seeded:O}', 'Requested', 'Uploader', 'Switch', 100, 0,
                   '{activation.Snapshot}'::jsonb, false, '{Seeded:O}', '{Seeded:O}');
                """);
            Rows.Add((activation.Id, activation.Tenant, "profile_activations", activation.Snapshot, Seeded));
        }

        await Execute(conn, "SELECT set_config('app.current_tenant_id', '', false);");
    }

    private async Task SeedStoreAsync(NpgsqlConnection conn, RepairDoubleConvertedMmolProfilesTests.Store store)
    {
        await Context(conn, store.Tenant);
        string Key(string? value) => value is null ? "NULL" : $"'{value}'";
        var correlation = store.Correlation is { } c ? $"'{c}'" : "NULL";
        var deleted = store.Deleted ? $"'{Seeded:O}'" : "NULL";
        var isfUpdated = store.IsfUpdated ?? Seeded;
        var isfTimestamp = store.IsfTimestamp ?? Seeded;
        await Execute(conn, $"""
            INSERT INTO therapy_settings
              (id, tenant_id, timestamp, profile_name, units, dia, carbs_hr, delay, per_gi_values, is_default,
               is_externally_managed, legacy_id, correlation_id, sys_created_at, sys_updated_at, deleted_at)
            VALUES ('{Guid.CreateVersion7()}', '{store.Tenant}', '{Seeded:O}', '{store.Name}', '{store.Units}', 5, 20, 20,
               false, false, false, {Key(store.LegacyId)}, {correlation}, '{Seeded:O}', '{Seeded:O}', {deleted});
            INSERT INTO target_range_schedules
              (id, tenant_id, timestamp, profile_name, entries_json, legacy_id, correlation_id, sys_created_at, sys_updated_at, deleted_at)
            VALUES ('{store.TargetId}', '{store.Tenant}', '{Seeded:O}', '{store.Name}', '{store.Targets}'::jsonb,
               {Key(store.LegacyId)}, {correlation}, '{Seeded:O}', '{Seeded:O}', {deleted});
            INSERT INTO sensitivity_schedules
              (id, tenant_id, timestamp, profile_name, entries_json, legacy_id, correlation_id, sys_created_at, sys_updated_at, deleted_at)
            VALUES ('{store.SensitivityId}', '{store.Tenant}', '{isfTimestamp:O}', '{store.Name}', '{store.Sensitivity}'::jsonb,
               {Key(store.LegacyId)}, {correlation}, '{Seeded:O}', '{isfUpdated:O}', {deleted});
            """);
        if (store.Audited is { } audit)
        {
            await Execute(conn, $"""
                INSERT INTO mutation_audit_log (id, tenant_id, entity_type, entity_id, action, changes, correlation_id, created_at)
                VALUES ('{Guid.CreateVersion7()}', '{store.Tenant}', 'TargetRangeSchedule', '{store.TargetId}', 'update',
                   '{Changes(audit.TargetsBefore, store.Targets)}'::jsonb, '{audit.TargetTrace}', '{Seeded:O}'),
                  ('{Guid.CreateVersion7()}', '{store.Tenant}', 'SensitivitySchedule', '{store.SensitivityId}', 'update',
                   '{Changes(audit.IsfBefore, store.Sensitivity)}'::jsonb, '{audit.IsfTrace}', '{Seeded:O}');
                """);
        }
        Rows.Add((store.TargetId, store.Tenant, "target_range_schedules", store.Targets, Seeded));
        Rows.Add((store.SensitivityId, store.Tenant, "sensitivity_schedules", store.Sensitivity, isfUpdated));
    }

    /// <summary>The <c>changes</c> <c>MutationAuditInterceptor</c> writes for an <c>EntriesJson</c> update.</summary>
    private static string Changes(string before, string after) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["EntriesJson"] = new Dictionary<string, string> { ["old"] = before, ["new"] = after },
        });

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
public class RepairDoubleConvertedMmolProfilesTests : IClassFixture<RepairDoubleConvertedMmolProfilesFixture>
{
    private readonly RepairDoubleConvertedMmolProfilesFixture _fixture;

    public RepairDoubleConvertedMmolProfilesTests(RepairDoubleConvertedMmolProfilesFixture fixture) => _fixture = fixture;

    internal sealed record Store(
        string Key, Guid Tenant, string Name, string Units, string? LegacyId, Guid? Correlation,
        string Targets, string Sensitivity, DateTime? IsfUpdated = null, bool Deleted = false,
        DateTime? IsfTimestamp = null, RepairDoubleConvertedMmolProfilesTests.Audit? Audited = null)
    {
        public Guid TargetId { get; } = Guid.CreateVersion7();
        public Guid SensitivityId { get; } = Guid.CreateVersion7();
    }

    internal sealed record Activation(string Key, Guid Id, Guid Tenant, string Snapshot);

    /// <summary>The audit rows of the upload that converted a store's targets and ISF.</summary>
    internal sealed record Audit(string TargetsBefore, string IsfBefore, string TargetTrace, string IsfTrace);

    private static readonly Guid TenantA = RepairDoubleConvertedMmolProfilesFixture.TenantA;
    private static readonly Guid TenantB = RepairDoubleConvertedMmolProfilesFixture.TenantB;

    private static string T(params (double, double)[] e) => RepairDoubleConvertedMmolProfilesFixture.Targets(e);
    private static string V(params double[] v) => RepairDoubleConvertedMmolProfilesFixture.Values(v);

    // 90/99 and 108/126 mg/dL with ISF 63 and 58, as a mmol profile of 5.0/5.5, 6.0/7.0 and 3.5/3.2 stores them.
    private static readonly string CorrectTargets = T((90, 99), (108, 126));
    private static readonly string CorrectIsf = V(63, 58);

    // The same after one read-and-reupload: round(m * 18.0182) of each.
    private static readonly string OnceTargets = T((1622, 1784), (1946, 2270));
    private static readonly string OnceIsf = V(1135, 1045);

    // After two: round(round(m * 18.0182) * 18.0182).
    private static readonly string TwiceTargets = T((29226, 32144), (35063, 40901));
    private static readonly string TwiceIsf = V(20451, 18829);

    internal static readonly Store[] Stores =
    [
        new("once", TenantA, "AAPS", "mmol", "docA:AAPS", Guid.CreateVersion7(), OnceTargets, OnceIsf),
        new("twice", TenantA, "Twice", "mmol/L", "docA:Twice", null, TwiceTargets, TwiceIsf),
        new("by correlation", TenantA, "Loop", "mmol", null, Guid.CreateVersion7(), OnceTargets, OnceIsf),
        new("correct mmol", TenantA, "Fine", "mmol", "docA:Fine", null, CorrectTargets, CorrectIsf),
        new("mg/dL", TenantA, "Mgdl", "mg/dl", "docA:Mgdl", null, OnceTargets, OnceIsf),
        new("not an image", TenantA, "Odd", "mmol", "docA:Odd", null, T((1623, 1785)), OnceIsf),
        new("isf edited since", TenantA, "Edited", "mmol", "docA:Edited", null, OnceTargets, V(54)),
        new("correct isf 90 beside converted targets", TenantA, "Child90", "mmol", "docA:Child90", null, OnceTargets, V(90)),
        new("correct isf 108 beside converted targets", TenantA, "Child108", "mmol", "docA:Child108", null, OnceTargets, V(108, 360)),
        new("isf rewritten through v4", TenantA, "Put", "mmol", "docA:Put", null, OnceTargets, OnceIsf,
            IsfUpdated: RepairDoubleConvertedMmolProfilesFixture.Seeded.AddDays(1)),
        new("isf converted more than its targets", TenantA, "More", "mmol", "docA:More", null, OnceTargets, TwiceIsf),
        new("isf above 400 beside correct targets", TenantA, "NoWitness", "mmol", "docA:NoWitness", null, CorrectTargets, OnceIsf),
        new("deleted", TenantA, "Gone", "mmol", "docA:Gone", null, OnceTargets, OnceIsf, Deleted: true),
        new("correct isf 23 and 25 mmol beside converted targets", TenantA, "Child23", "mmol", "docA:Child23", null,
            OnceTargets, V(414, 450)),
        new("isf 414 and 450 the audit log shows converted", TenantA, "Audited", "mmol", "docA:Audited", null,
            OnceTargets, V(414, 450), Audited: new Audit(CorrectTargets, V(23, 25), "trace-1", "trace-1")),
        new("isf 414 and 450 converted in another request", TenantA, "Apart", "mmol", "docA:Apart", null,
            OnceTargets, V(414, 450), Audited: new Audit(CorrectTargets, V(23, 25), "trace-2", "trace-3")),
        new("isf 1009, a 56 mg/dL/U image", TenantA, "Edge1009", "mmol", "docA:Edge1009", null, OnceTargets, V(1009)),
        new("isf 991, a 55 mg/dL/U image", TenantA, "Edge991", "mmol", "docA:Edge991", null, OnceTargets, V(991)),
        new("isf 1135 beside 991", TenantA, "Mixed", "mmol", "docA:Mixed", null, OnceTargets, V(1135, 991)),
        new("isf 1000", TenantA, "Edge1000", "mmol", "docA:Edge1000", null, OnceTargets, V(1000)),
        new("isf 1001", TenantA, "Edge1001", "mmol", "docA:Edge1001", null, OnceTargets, V(1001)),
        new("isf with another timestamp", TenantA, "Later", "mmol", "docA:Later", null, OnceTargets, OnceIsf,
            IsfTimestamp: RepairDoubleConvertedMmolProfilesFixture.Seeded.AddHours(1)),
        new("isf 59 s after its targets", TenantA, "Within", "mmol", "docA:Within", null, OnceTargets, OnceIsf,
            IsfUpdated: RepairDoubleConvertedMmolProfilesFixture.Seeded.AddSeconds(59)),
        new("isf 61 s after its targets", TenantA, "Outside", "mmol", "docA:Outside", null, OnceTargets, OnceIsf,
            IsfUpdated: RepairDoubleConvertedMmolProfilesFixture.Seeded.AddSeconds(61)),
        new("other tenant, same key, correct", TenantB, "AAPS", "mmol", "docA:AAPS", null, CorrectTargets, CorrectIsf),
        new("other tenant, once", TenantB, "Night", "mmol", "docB:Night", null, OnceTargets, OnceIsf),
    ];

    internal static readonly Activation[] Activations =
    [
        new("snapshot once", Guid.CreateVersion7(), TenantA,
            RepairDoubleConvertedMmolProfilesFixture.Snapshot("mmol", OnceTargets, OnceIsf)),
        new("snapshot correct isf", Guid.CreateVersion7(), TenantA,
            RepairDoubleConvertedMmolProfilesFixture.Snapshot("mmol", OnceTargets, V(108, 360))),
        new("snapshot isf that could be correct", Guid.CreateVersion7(), TenantA,
            RepairDoubleConvertedMmolProfilesFixture.Snapshot("mmol", OnceTargets, V(414, 450))),
        new("snapshot correct", Guid.CreateVersion7(), TenantA,
            RepairDoubleConvertedMmolProfilesFixture.Snapshot("mmol", CorrectTargets, CorrectIsf)),
        new("snapshot mg/dL", Guid.CreateVersion7(), TenantB,
            RepairDoubleConvertedMmolProfilesFixture.Snapshot("mg/dl", OnceTargets, OnceIsf)),
    ];

    private static Store StoreOf(string key) => Stores.Single(s => s.Key == key);

    private static double[] Numbers(string json, string key) =>
        JsonNode.Parse(json)!.AsArray().Select(e => e![key]!.GetValue<double>()).ToArray();

    private (double[] Low, double[] High, double[] Isf) After(Store store)
    {
        var targets = _fixture.AfterUp[store.TargetId].Json;
        return (Numbers(targets, "Low"), Numbers(targets, "High"), Numbers(_fixture.AfterUp[store.SensitivityId].Json, "Value"));
    }

    private void ShouldBeRepaired(Store store)
    {
        var (low, high, isf) = After(store);
        low.Should().Equal(90, 108);
        high.Should().Equal(99, 126);
        isf.Should().Equal(63, 58);
        _fixture.AfterUp[store.TargetId].Updated.Should().BeAfter(RepairDoubleConvertedMmolProfilesFixture.Seeded);
        _fixture.AfterUp[store.SensitivityId].Updated.Should().BeAfter(RepairDoubleConvertedMmolProfilesFixture.Seeded);
    }

    private void ShouldBeUntouched(Guid id)
    {
        var seeded = _fixture.Rows.Single(r => r.Id == id);
        JsonNode.DeepEquals(JsonNode.Parse(_fixture.AfterUp[id].Json), JsonNode.Parse(seeded.Json)).Should().BeTrue();
        _fixture.AfterUp[id].Updated.Should().Be(seeded.Updated);
    }

    [Fact]
    public void AStoreConvertedOnceMore_HasItsTargetsAndIsfRepaired() => ShouldBeRepaired(StoreOf("once"));

    [Fact]
    public void AStoreConvertedTwiceMore_HasItsTargetsAndIsfRepaired() => ShouldBeRepaired(StoreOf("twice"));

    [Fact]
    public void AStoreFoundByCorrelation_HasItsTargetsAndIsfRepaired() => ShouldBeRepaired(StoreOf("by correlation"));

    [Fact]
    public void ARepairKeepsEveryOtherFieldOfEachEntry()
    {
        var entry = JsonNode.Parse(_fixture.AfterUp[StoreOf("once").TargetId].Json)!.AsArray()[1]!;
        entry["Time"]!.GetValue<string>().Should().Be("06:00");
        entry["TimeAsSeconds"]!.GetValue<int>().Should().Be(21600);
    }

    [Theory]
    [InlineData("correct mmol")]
    [InlineData("mg/dL")]
    [InlineData("not an image")]
    [InlineData("other tenant, same key, correct")]
    public void AStoreTheTestDoesNotSelectOrCannotUndo_IsUntouched(string key)
    {
        ShouldBeUntouched(StoreOf(key).TargetId);
        ShouldBeUntouched(StoreOf(key).SensitivityId);
    }

    [Theory]
    [InlineData("isf edited since")]
    [InlineData("correct isf 90 beside converted targets")]
    [InlineData("correct isf 108 beside converted targets")]
    [InlineData("isf rewritten through v4")]
    [InlineData("isf converted more than its targets")]
    [InlineData("correct isf 23 and 25 mmol beside converted targets")]
    [InlineData("isf 414 and 450 converted in another request")]
    [InlineData("isf 414 and 450 the audit log shows converted")]
    [InlineData("isf 991, a 55 mg/dL/U image")]
    [InlineData("isf 1135 beside 991")]
    [InlineData("isf 1000")]
    [InlineData("isf 1001")]
    [InlineData("isf with another timestamp")]
    [InlineData("isf 61 s after its targets")]
    public void AnIsfWithoutTheWitnessOrTheSameConversions_IsLeftWhileItsTargetsAreRepaired(string key)
    {
        var store = StoreOf(key);
        var (low, high, _) = After(store);
        low.Should().Equal(90, 108);
        high.Should().Equal(99, 126);
        ShouldBeUntouched(store.SensitivityId);
    }

    [Theory]
    [InlineData("isf above 400 beside correct targets")]
    [InlineData("deleted")]
    public void AStoreWhoseTargetsAreNotSelected_IsUntouched(string key)
    {
        ShouldBeUntouched(StoreOf(key).TargetId);
        ShouldBeUntouched(StoreOf(key).SensitivityId);
    }

    [Theory]
    [InlineData("snapshot correct isf", new double[] { 108, 360 })]
    [InlineData("snapshot isf that could be correct", new double[] { 414, 450 })]
    public void AMmolActivationSnapshotWithAnIsfThatCouldBeCorrect_HasOnlyItsTargetsRepaired(string key, double[] isf)
    {
        var snapshot = JsonNode.Parse(_fixture.AfterUp[Activations.Single(a => a.Key == key).Id].Json)!;
        Numbers(snapshot["Targets"]!.ToJsonString(), "Low").Should().Equal(90, 108);
        Numbers(snapshot["Sensitivity"]!.ToJsonString(), "Value").Should().Equal(isf);
    }

    [Fact]
    public void EachTenantIsRepaired_AndOnlyItsOwnRows() => ShouldBeRepaired(StoreOf("other tenant, once"));

    [Fact]
    public void AnIsfWrittenWithinAMinuteOfItsTargets_IsRepaired() => ShouldBeRepaired(StoreOf("isf 59 s after its targets"));

    [Fact]
    public void AnIsfAbove1000BeforeItsLastConversion_IsRepaired()
    {
        var (low, _, isf) = After(StoreOf("isf 1009, a 56 mg/dL/U image"));
        low.Should().Equal(90, 108);
        isf.Should().Equal(56);
    }

    [Fact]
    public void AMmolActivationSnapshot_HasItsTargetsAndIsfRepaired_AndKeepsTheRest()
    {
        var id = Activations.Single(a => a.Key == "snapshot once").Id;
        var snapshot = JsonNode.Parse(_fixture.AfterUp[id].Json)!;
        Numbers(snapshot["Targets"]!.ToJsonString(), "Low").Should().Equal(90, 108);
        Numbers(snapshot["Targets"]!.ToJsonString(), "High").Should().Equal(99, 126);
        Numbers(snapshot["Sensitivity"]!.ToJsonString(), "Value").Should().Equal(63, 58);
        Numbers(snapshot["Basal"]!.ToJsonString(), "Value").Should().Equal(0.8);
        Numbers(snapshot["CarbRatio"]!.ToJsonString(), "Value").Should().Equal(9.1);
        snapshot["Units"]!.GetValue<string>().Should().Be("mmol");
    }

    [Theory]
    [InlineData("snapshot correct")]
    [InlineData("snapshot mg/dL")]
    public void AnActivationSnapshotTheTestDoesNotSelect_IsUntouched(string key) =>
        ShouldBeUntouched(Activations.Single(a => a.Key == key).Id);

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
