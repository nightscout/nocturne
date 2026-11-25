using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Integration.Migration;

/// <summary>
/// The schedules and activation snapshots of two tenants as the v0.3.0 baseline's raw mmol repair leaves
/// them: a store it could prove raw holds mg/dL values written in one batch, and every other store holds
/// what was uploaded.
/// </summary>
public sealed class RawMmolProfileStoresFixture : IAsyncLifetime
{
    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111112355");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222355");

    internal static readonly DateTime Seeded = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>When the repair wrote the stores it converted.</summary>
    private static readonly DateTime Repaired = Seeded.AddDays(150);

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

    internal sealed record Activation(Guid Id, Guid Tenant, string Snapshot, bool Deleted = false);

    private static string T(params (double Low, double High)[] entries) => JsonSerializer.Serialize(
        entries.Select((e, i) => new { Time = $"{i * 6:00}:00", Low = e.Low, High = e.High, TimeAsSeconds = i * 21600 }));

    private static string V(params double[] values) => JsonSerializer.Serialize(
        values.Select((v, i) => new { Time = $"{i * 6:00}:00", Value = v, TimeAsSeconds = i * 21600 }));

    private static string Snapshot(string units, string targets, string sensitivity) =>
        $$"""{"Basal":{{V(0.8)}},"CarbRatio":{{V(9.1)}},"Sensitivity":{{sensitivity}},"Targets":{{targets}},"Units":"{{units}}","Dia":5}""";

    // A mmol profile of targets 5.0/5.5 and 6.0/7.0 and ISF 3.5 and 3.2, as uploaded and in mg/dL.
    private static readonly string RawTargets = T((5.0, 5.5), (6.0, 7.0));
    private static readonly string RawIsf = V(3.5, 3.2);
    private static readonly string CorrectTargets = T((90, 99), (108, 126));
    private static readonly string CorrectIsf = V(63, 58);

    private static readonly Guid Tainted = Guid.CreateVersion7();

    private static Store Converted(string key, Guid tenant, string name, string legacyId, string isf) =>
        new(key, tenant, name, "mmol", legacyId, null, CorrectTargets, isf, IsfUpdated: Repaired, TargetUpdated: Repaired);

    internal static readonly Store[] Stores =
    [
        Converted("converted", TenantA, "AAPS", "docA:AAPS", CorrectIsf),
        Converted("converted, isf 9", TenantA, "Isf05", "docA:Isf05", V(9)),
        Converted("other tenant, converted", TenantB, "Night", "docB:Night", CorrectIsf),
        new("other tenant, no isf", TenantB, "NightNoIsf", "mmol", "docB:NightNoIsf", null, RawTargets, null),
        new("other tenant, same key, correct", TenantB, "AAPS", "mmol", "docA:AAPS", null, CorrectTargets, CorrectIsf),

        new("isf 30", TenantA, "Isf30", "mmol", "docA:Isf30", null, RawTargets, V(30)),
        new("isf from another batch", TenantA, "Later", "mmol", "docA:Later", null, RawTargets, RawIsf,
            IsfTimestamp: Seeded.AddHours(1)),
        new("no isf", TenantA, "NoIsf", "mmol", "docA:NoIsf", null, RawTargets, null),

        new("correct mmol", TenantA, "Fine", "mmol", "docA:Fine", null, CorrectTargets, CorrectIsf),
        new("mg/dL", TenantA, "Mgdl", "mg/dl", "docA:Mgdl", null, RawTargets, RawIsf),
        new("deleted", TenantA, "Gone", "mmol", "docA:Gone", null, RawTargets, RawIsf, Deleted: true),
        new("target 1.9", TenantA, "Low19", "mmol", "docA:Low19", null, T((1.9, 5.5), (6.0, 7.0)), RawIsf),
        new("target as a string", TenantA, "TargetText", "mmol", "docA:TargetText", null,
            """[{"Time":"00:00","Low":"5.0","High":5.5,"TimeAsSeconds":0}]""", RawIsf),

        new("tainted isf, by legacy id", TenantA, "Tainted", "mmol", "docA:Tainted", Tainted, RawTargets, RawIsf),
        new("tainted isf, by correlation, another batch", TenantA, "Tainted", "mmol", null, Tainted, RawTargets, null,
            TargetUpdated: Seeded.AddMinutes(2), Owner: false),

        new("older store beside a newer orphan raw isf", TenantA, "Orphaned", "mmol", "d1:Orphaned", null, RawTargets, RawIsf),
        new("newer orphan raw isf", TenantA, "Orphaned", "mmol", "d2:Orphaned", null, null, V(3.0),
            Owner: false, Timestamp: Seeded.AddDays(1)),
        Converted("converted store beside a newer mmol store", TenantA, "Corrected", "d1:Corrected", CorrectIsf),
        new("newer converted mmol store with isf 15", TenantA, "Corrected", "mmol", "d2:Corrected", null, CorrectTargets, V(15),
            Timestamp: Seeded.AddDays(1)),
        new("raw store beside a store whose targets were retimed", TenantA, "Retimed", "mmol", "d1:Retimed", null, RawTargets, RawIsf),
        new("store with targets edited later beside its raw isf", TenantA, "Retimed", "mmol", "d2:Retimed", null, T((100, 110)), V(3.0),
            Timestamp: Seeded.AddDays(1), TargetTimestamp: Seeded.AddDays(2)),
        new("store for the decomposer upload", TenantA, "Upload", "mmol", "d3:Upload", null, T((100, 110)), V(3.0),
            Timestamp: Seeded.AddDays(1), TargetTimestamp: Seeded.AddDays(2)),
        new("raw targets and isf 12 to be corrected by hand", TenantA, "HandEdit", "mmol", "d4:HandEdit", null, RawTargets, V(12),
            IsfUpdated: Seeded.AddSeconds(61)),
        new("raw targets and isf 12 to be uploaded again", TenantA, "Reupload", "mmol", "d5:Reupload", null, RawTargets, V(12),
            IsfUpdated: Seeded.AddSeconds(61)),
        new("mmol store with mg/dL targets and isf 5", TenantA, "Under9", "mmol", "docA:Under9", null, CorrectTargets, V(5)),
    ];

    internal static readonly Activation[] Activations =
    [
        new(Guid.CreateVersion7(), TenantA, Snapshot("mmol", CorrectTargets, CorrectIsf)),
        new(Guid.CreateVersion7(), TenantA, Snapshot("mmol", RawTargets, V(30))),
        new(Guid.CreateVersion7(), TenantA, Snapshot("mmol", RawTargets, "[]")),
        new(Guid.CreateVersion7(), TenantB, Snapshot("mg/dl", RawTargets, RawIsf)),
        new(Guid.CreateVersion7(), TenantB, Snapshot("mmol", RawTargets, RawIsf), Deleted: true),
    ];

    /// <summary>The database as the runtime role.</summary>
    internal string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateMigratedDatabaseAsync("raw_mmol_profile_stores");
        AppConnectionString = database.AppConnectionString;

        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{TenantA}', 'tenant-a', 'Tenant A', true, now(), now()),
              ('{TenantB}', 'tenant-b', 'Tenant B', true, now(), now());
            """);

        foreach (var store in Stores)
            await SeedStoreAsync(conn, store);
        foreach (var activation in Activations)
        {
            await conn.PinTenantAsync(activation.Tenant);
            var deleted = activation.Deleted ? $"'{Seeded:O}'" : "NULL";
            await conn.ExecuteAsync($"""
                INSERT INTO profile_activations
                  (id, tenant_id, start_timestamp, phase, origin, base_profile_name, percentage, timeshift_ms,
                   snapshot, snapshot_is_customized, sys_created_at, sys_updated_at, deleted_at)
                VALUES ('{activation.Id}', '{activation.Tenant}', '{Seeded:O}', 'Requested', 'Uploader', 'Switch', 100, 0,
                   '{activation.Snapshot}'::jsonb, false, '{Seeded:O}', '{Seeded:O}', {deleted});
                """);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task SeedStoreAsync(NpgsqlConnection conn, Store store)
    {
        await conn.PinTenantAsync(store.Tenant);
        string Key(string? value) => value is null ? "NULL" : $"'{value}'";
        var correlation = store.Correlation is { } c ? $"'{c}'" : "NULL";
        var deleted = store.Deleted ? $"'{Seeded:O}'" : "NULL";
        var timestamp = store.Timestamp ?? Seeded;
        if (store.Owner)
        {
            await conn.ExecuteAsync($"""
                INSERT INTO therapy_settings
                  (id, tenant_id, timestamp, profile_name, units, dia, carbs_hr, delay, per_gi_values, is_default,
                   is_externally_managed, legacy_id, correlation_id, sys_created_at, sys_updated_at, deleted_at)
                VALUES ('{Guid.CreateVersion7()}', '{store.Tenant}', '{timestamp:O}', '{store.Name}', '{store.Units}', 5, 20, 20,
                   false, false, false, {Key(store.LegacyId)}, {correlation}, '{Seeded:O}', '{Seeded:O}', {deleted});
                """);
        }
        if (store.Targets is not null)
        {
            await conn.ExecuteAsync($"""
                INSERT INTO target_range_schedules
                  (id, tenant_id, timestamp, profile_name, entries_json, legacy_id, correlation_id, sys_created_at, sys_updated_at, deleted_at)
                VALUES ('{store.TargetId}', '{store.Tenant}', '{store.TargetTimestamp ?? timestamp:O}', '{store.Name}', '{store.Targets}'::jsonb,
                   {Key(store.LegacyId)}, {correlation}, '{Seeded:O}', '{store.TargetUpdated ?? Seeded:O}', {deleted});
                """);
        }
        if (store.Sensitivity is not null)
        {
            await conn.ExecuteAsync($"""
                INSERT INTO sensitivity_schedules
                  (id, tenant_id, timestamp, profile_name, entries_json, legacy_id, correlation_id, sys_created_at, sys_updated_at, deleted_at)
                VALUES ('{store.SensitivityId}', '{store.Tenant}', '{store.IsfTimestamp ?? timestamp:O}', '{store.Name}', '{store.Sensitivity}'::jsonb,
                   {Key(store.LegacyId)}, {correlation}, '{Seeded:O}', '{store.IsfUpdated ?? Seeded:O}', {deleted});
                """);
        }
    }
}

/// <summary>
/// Runs <see cref="RawMmolProfileDetector"/> as the runtime role over the schedules the raw mmol repair
/// converted and left, so it lists what the repair could not prove.
/// </summary>
[Trait("Category", "Integration")]
public class RawMmolProfileDetectorTests : IClassFixture<RawMmolProfileStoresFixture>, IAsyncLifetime
{
    private readonly RawMmolProfileStoresFixture _fixture;
    private NpgsqlDataSource _dataSource = null!;

    public RawMmolProfileDetectorTests(RawMmolProfileStoresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _dataSource = new NpgsqlDataSourceBuilder(_fixture.AppConnectionString).Build();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    private INocturneDbContextFactory Contexts(Guid tenant) =>
        new OptionsFactory(TestContexts.Options(_dataSource)).AsNocturneFactory(tenant);

    private RawMmolProfileDetector Detector(Guid tenant) => new(Contexts(tenant));

    private static RawMmolProfileStoresFixture.Store StoreOf(string key) =>
        RawMmolProfileStoresFixture.Stores.Single(s => s.Key == key);

    private Task<IReadOnlyList<RawMmolSchedule>> DetectAsync(Guid tenant) => Detector(tenant).DetectAsync();

    [Fact]
    public async Task ATargetScheduleTheRepairLeftRaw_IsListedWithItsStoredValues()
    {
        var store = StoreOf("no isf");

        var item = (await DetectAsync(RawMmolProfileStoresFixture.TenantA)).Single(i => i.Id == store.TargetId);

        item.Kind.Should().Be(RawMmolScheduleKind.TargetRange);
        item.Reason.Should().Be(RawMmolReason.TargetInMmolBand);
        item.ProfileName.Should().Be("NoIsf");
        item.LegacyId.Should().Be("docA:NoIsf");
        item.CanDismiss.Should().BeFalse();
        item.Entries.Select(e => (e.Time, e.Low, e.High)).Should().Equal(("00:00", 5.0, 5.5), ("06:00", 6.0, 7.0));
    }

    [Theory]
    [InlineData("isf 30")]
    [InlineData("isf from another batch")]
    [InlineData("tainted isf, by correlation, another batch")]
    [InlineData("older store beside a newer orphan raw isf")]
    [InlineData("target 1.9")]
    [InlineData("target as a string")]
    public async Task EveryTargetScheduleStillHoldingATargetUnder22_IsListed(string key)
    {
        var listed = await DetectAsync(RawMmolProfileStoresFixture.TenantA);

        listed.Should().ContainSingle(i => i.Id == StoreOf(key).TargetId && i.Reason == RawMmolReason.TargetInMmolBand);
    }

    [Theory]
    [InlineData("mg/dL")]
    [InlineData("converted")]
    [InlineData("converted, isf 9")]
    [InlineData("correct mmol")]
    [InlineData("newer converted mmol store with isf 15")]
    [InlineData("deleted")]
    public async Task AStoreThatIsMgdlOrProvenConverted_IsNotListed(string key)
    {
        var store = StoreOf(key);

        var listed = await DetectAsync(RawMmolProfileStoresFixture.TenantA);

        listed.Should().NotContain(i => i.Id == store.TargetId || i.Id == store.SensitivityId);
    }

    /// <summary>
    /// A re-upload writes a store's ISF and targets in one batch at one timestamp, which is the proof:
    /// an ISF of 9 to 22 mg/dL/U beside targets of its own timestamp and none under 22.2 is not listed.
    /// </summary>
    [Fact]
    public async Task AnIsfWrittenInOneBatchWithMgdlTargets_IsNotListedThoughItIsUnder22()
    {
        var store = StoreOf("converted, isf 9");

        var listed = await DetectAsync(RawMmolProfileStoresFixture.TenantA);

        listed.Should().NotContain(i => i.Id == store.SensitivityId);
    }

    [Theory]
    [InlineData("store with targets edited later beside its raw isf")]
    [InlineData("mmol store with mg/dL targets and isf 5")]
    [InlineData("newer orphan raw isf")]
    public async Task AnIsfNothingProvesConverted_IsListed(string key)
    {
        var listed = await DetectAsync(RawMmolProfileStoresFixture.TenantA);

        listed.Should().ContainSingle(i => i.Id == StoreOf(key).SensitivityId)
            .Which.Reason.Should().Be(RawMmolReason.IsfMayBeMmol);
    }

    [Fact]
    public async Task ADismissedIsf_IsNoLongerListed_AndItsStoreTargetsStillAre()
    {
        var store = StoreOf("tainted isf, by legacy id");
        var detector = Detector(RawMmolProfileStoresFixture.TenantA);
        (await detector.DetectAsync()).Should().ContainSingle(i => i.Id == store.SensitivityId)
            .Which.CanDismiss.Should().BeTrue();

        (await detector.DismissIsfAsync(store.SensitivityId)).Should().BeTrue();

        var listed = await detector.DetectAsync();
        listed.Should().NotContain(i => i.Id == store.SensitivityId);
        listed.Should().Contain(i => i.Id == store.TargetId);
    }

    [Fact]
    public async Task AnIsfWithNoTherapySettingsRow_CannotBeDismissed()
    {
        var orphan = StoreOf("newer orphan raw isf");
        var detector = Detector(RawMmolProfileStoresFixture.TenantA);

        (await detector.DetectAsync()).Single(i => i.Id == orphan.SensitivityId).CanDismiss.Should().BeFalse();
        (await detector.DismissIsfAsync(orphan.SensitivityId)).Should().BeFalse();
        (await detector.DetectAsync()).Should().Contain(i => i.Id == orphan.SensitivityId);
    }

    [Fact]
    public async Task AnUploadOverADismissedStore_ClearsTheDismiss()
    {
        var store = StoreOf("store with targets edited later beside its raw isf");
        var contexts = Contexts(RawMmolProfileStoresFixture.TenantA);
        var detector = new RawMmolProfileDetector(contexts);
        (await detector.DismissIsfAsync(store.SensitivityId)).Should().BeTrue();
        (await detector.DetectAsync()).Should().NotContain(i => i.Id == store.SensitivityId);

        var repository = new TherapySettingsRepository(
            contexts, SystemAuditContext.ForService("test"), NullLogger<TherapySettingsRepository>.Instance);
        await repository.BulkUpsertByLegacyIdAsync(
            [new TherapySettings { LegacyId = "d2:Retimed", ProfileName = "Retimed", Units = "mmol", Timestamp = RawMmolProfileStoresFixture.Seeded.AddDays(1) }],
            WriteOrigin.Live);

        (await detector.DetectAsync()).Should().Contain(i => i.Id == store.SensitivityId);
    }

    private static async Task<string?> DismissKeyOfAsync(INocturneDbContextFactory contexts, string legacyId)
    {
        await using var db = await contexts.CreateAsync();
        var json = await db.TherapySettings.Where(t => t.LegacyId == legacyId).Select(t => t.AdditionalPropertiesJson).SingleAsync();
        return json is null ? null : System.Text.Json.Nodes.JsonNode.Parse(json)?[RawMmolProfileDetector.IsfIsMgdlKey]?.ToString();
    }

    private static ProfileDecomposer DecomposerOver(INocturneDbContextFactory contexts)
    {
        var audit = SystemAuditContext.ForService("test");
        return new ProfileDecomposer(
            new TherapySettingsRepository(contexts, audit, NullLogger<TherapySettingsRepository>.Instance),
            new BasalScheduleRepository(contexts, audit, NullLogger<BasalScheduleRepository>.Instance),
            new CarbRatioScheduleRepository(contexts, audit, NullLogger<CarbRatioScheduleRepository>.Instance),
            new SensitivityScheduleRepository(contexts, audit, NullLogger<SensitivityScheduleRepository>.Instance),
            new TargetRangeScheduleRepository(contexts, audit, NullLogger<TargetRangeScheduleRepository>.Instance),
            Mock.Of<ITempBasalRateReresolver>(),
            Mock.Of<ILoopClosedLoopStamper>(),
            audit,
            NullLogger<ProfileDecomposer>.Instance);
    }

    private static Profile MgdlProfile(string id, string name, double isf) => new()
    {
        Id = id,
        Mills = new DateTimeOffset(RawMmolProfileStoresFixture.Seeded.AddDays(2), TimeSpan.Zero).ToUnixTimeMilliseconds(),
        DefaultProfile = name,
        Units = "mg/dl",
        Store = new Dictionary<string, ProfileData>
        {
            [name] = new()
            {
                Units = "mg/dl",
                Timezone = "UTC",
                Basal = [new TimeValue { Time = "00:00", Value = 1.0 }],
                CarbRatio = [new TimeValue { Time = "00:00", Value = 10.0 }],
                Sens = [new TimeValue { Time = "00:00", Value = isf }],
                TargetLow = [new TimeValue { Time = "00:00", Value = 100 }],
                TargetHigh = [new TimeValue { Time = "00:00", Value = 110 }],
            },
        },
    };

    [Fact]
    public async Task AnUploadThroughTheDecomposerOverADismissedStore_DoesNotThrowAndClearsTheDismiss()
    {
        var store = StoreOf("store for the decomposer upload");
        var contexts = Contexts(RawMmolProfileStoresFixture.TenantA);
        var detector = new RawMmolProfileDetector(contexts);
        (await detector.DismissIsfAsync(store.SensitivityId)).Should().BeTrue();
        (await detector.DetectAsync()).Should().NotContain(i => i.Id == store.SensitivityId);
        (await DismissKeyOfAsync(contexts, "d3:Upload")).Should().NotBeNull();

        await DecomposerOver(contexts).Invoking(d => d.DecomposeBatchAsync([MgdlProfile("d3", "Upload", 3.0)], WriteOrigin.Live))
            .Should().NotThrowAsync();

        (await DismissKeyOfAsync(contexts, "d3:Upload")).Should().BeNull();
    }

    /// <summary>
    /// A hand edit of the targets keeps the store's timestamp and writes later than the ISF, which is not
    /// the one batch an upload writes, so the ISF the owner has not corrected stays listed.
    /// </summary>
    [Fact]
    public async Task AnIsfInTheMgdlRangeStaysListed_WhenItsTargetsAreCorrectedByAnEditLater()
    {
        var store = StoreOf("raw targets and isf 12 to be corrected by hand");
        var contexts = Contexts(RawMmolProfileStoresFixture.TenantA);
        var detector = new RawMmolProfileDetector(contexts);
        (await detector.DetectAsync()).Should().ContainSingle(i => i.Id == store.SensitivityId);

        var targets = new TargetRangeScheduleRepository(
            contexts, SystemAuditContext.ForService("test"), NullLogger<TargetRangeScheduleRepository>.Instance);
        var stored = await targets.GetByIdAsync(store.TargetId);
        stored!.Entries = [new TargetRangeEntry { Time = "00:00", Low = 90, High = 99 }];
        await targets.UpdateAsync(store.TargetId, stored, WriteOrigin.Live);

        var listed = await detector.DetectAsync();
        listed.Should().NotContain(i => i.Id == store.TargetId);
        listed.Should().ContainSingle(i => i.Id == store.SensitivityId);
    }

    [Fact]
    public async Task AnIsfInTheMgdlRangeIsNoLongerListed_OnceTheWholeStoreIsUploadedAgain()
    {
        var store = StoreOf("raw targets and isf 12 to be uploaded again");
        var contexts = Contexts(RawMmolProfileStoresFixture.TenantA);
        var detector = new RawMmolProfileDetector(contexts);
        (await detector.DetectAsync()).Should().ContainSingle(i => i.Id == store.SensitivityId);

        await DecomposerOver(contexts).DecomposeBatchAsync([MgdlProfile("d5", "Reupload", 216)], WriteOrigin.Live);

        (await detector.DetectAsync()).Should().NotContain(i => i.Id == store.SensitivityId || i.Id == store.TargetId);
    }

    [Fact]
    public async Task DismissingAScheduleThatDoesNotExist_ReportsNotFound() =>
        (await Detector(RawMmolProfileStoresFixture.TenantA).DismissIsfAsync(Guid.CreateVersion7())).Should().BeFalse();

    [Fact]
    public async Task OnlyLiveSchedulesAreListed_NeverAnActivationSnapshot()
    {
        var schedules = RawMmolProfileStoresFixture.Stores.SelectMany(s => new[] { s.TargetId, s.SensitivityId }).ToHashSet();

        var listed = await DetectAsync(RawMmolProfileStoresFixture.TenantA);

        listed.Should().NotBeEmpty();
        listed.Select(i => i.Id).Should().OnlyContain(id => schedules.Contains(id));
        listed.Select(i => i.Id).Should().NotIntersectWith(RawMmolProfileStoresFixture.Activations.Select(a => a.Id));
    }

    [Fact]
    public async Task EachTenantSeesOnlyItsOwnSchedules()
    {
        var other = StoreOf("other tenant, no isf");

        var listedA = await DetectAsync(RawMmolProfileStoresFixture.TenantA);
        var listedB = await DetectAsync(RawMmolProfileStoresFixture.TenantB);

        listedA.Should().NotContain(i => i.Id == other.TargetId);
        listedB.Select(i => i.Id).Should().Equal(other.TargetId);
    }

    private sealed class OptionsFactory(DbContextOptions<NocturneDbContext> options) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }
}
