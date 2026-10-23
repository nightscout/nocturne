using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
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
/// Runs <see cref="RawMmolProfileDetector"/> as the runtime role over the schedules
/// <c>RepairRawMmolProfiles</c> converted and left, so it lists what the migration could not prove.
/// </summary>
[Trait("Category", "Integration")]
public class RawMmolProfileDetectorTests : IClassFixture<RepairRawMmolProfilesFixture>, IAsyncLifetime
{
    private readonly RepairRawMmolProfilesFixture _fixture;
    private NpgsqlDataSource _dataSource = null!;

    public RawMmolProfileDetectorTests(RepairRawMmolProfilesFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        _dataSource = new NpgsqlDataSourceBuilder(_fixture.AppConnectionString).Build();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    private INocturneDbContextFactory Contexts(Guid tenant) =>
        new OptionsFactory(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_dataSource)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options).AsNocturneFactory(tenant);

    private RawMmolProfileDetector Detector(Guid tenant) => new(Contexts(tenant));

    private static RepairRawMmolProfilesTests.Store StoreOf(string key) =>
        RepairRawMmolProfilesTests.Stores.Single(s => s.Key == key);

    private Task<IReadOnlyList<RawMmolSchedule>> DetectAsync(Guid tenant) => Detector(tenant).DetectAsync();

    [Fact]
    public async Task ATargetScheduleTheMigrationLeftRaw_IsListedWithItsStoredValues()
    {
        var store = StoreOf("no isf");

        var item = (await DetectAsync(RepairRawMmolProfilesFixture.TenantA)).Single(i => i.Id == store.TargetId);

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
        var listed = await DetectAsync(RepairRawMmolProfilesFixture.TenantA);

        listed.Should().ContainSingle(i => i.Id == StoreOf(key).TargetId && i.Reason == RawMmolReason.TargetInMmolBand);
    }

    [Theory]
    [InlineData("mg/dL")]
    [InlineData("raw")]
    [InlineData("isf 0.5")]
    [InlineData("correct mmol")]
    [InlineData("newer converted mmol store with isf 15")]
    [InlineData("deleted")]
    public async Task AStoreThatIsMgdlOrProvenConverted_IsNotListed(string key)
    {
        var store = StoreOf(key);

        var listed = await DetectAsync(RepairRawMmolProfilesFixture.TenantA);

        listed.Should().NotContain(i => i.Id == store.TargetId || i.Id == store.SensitivityId);
    }

    /// <summary>
    /// A re-upload writes a store's ISF and targets in one batch at one timestamp, which is the proof:
    /// an ISF of 9 to 22 mg/dL/U beside targets of its own timestamp and none under 22.2 is not listed.
    /// </summary>
    [Fact]
    public async Task AnIsfWrittenInOneBatchWithMgdlTargets_IsNotListedThoughItIsUnder22()
    {
        var store = StoreOf("isf 0.5");

        var listed = await DetectAsync(RepairRawMmolProfilesFixture.TenantA);

        listed.Should().NotContain(i => i.Id == store.SensitivityId);
    }

    [Theory]
    [InlineData("store with targets edited later beside its raw isf")]
    [InlineData("mmol store with mg/dL targets and isf 5")]
    [InlineData("newer orphan raw isf")]
    public async Task AnIsfNothingProvesConverted_IsListed(string key)
    {
        var listed = await DetectAsync(RepairRawMmolProfilesFixture.TenantA);

        listed.Should().ContainSingle(i => i.Id == StoreOf(key).SensitivityId)
            .Which.Reason.Should().Be(RawMmolReason.IsfMayBeMmol);
    }

    [Fact]
    public async Task ADismissedIsf_IsNoLongerListed_AndItsStoreTargetsStillAre()
    {
        var store = StoreOf("tainted isf, by legacy id");
        var detector = Detector(RepairRawMmolProfilesFixture.TenantA);
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
        var detector = Detector(RepairRawMmolProfilesFixture.TenantA);

        (await detector.DetectAsync()).Single(i => i.Id == orphan.SensitivityId).CanDismiss.Should().BeFalse();
        (await detector.DismissIsfAsync(orphan.SensitivityId)).Should().BeFalse();
        (await detector.DetectAsync()).Should().Contain(i => i.Id == orphan.SensitivityId);
    }

    [Fact]
    public async Task AnUploadOverADismissedStore_ClearsTheDismiss()
    {
        var store = StoreOf("store with targets edited later beside its raw isf");
        var contexts = Contexts(RepairRawMmolProfilesFixture.TenantA);
        var detector = new RawMmolProfileDetector(contexts);
        (await detector.DismissIsfAsync(store.SensitivityId)).Should().BeTrue();
        (await detector.DetectAsync()).Should().NotContain(i => i.Id == store.SensitivityId);

        var repository = new TherapySettingsRepository(
            contexts, SystemAuditContext.ForService("test"), NullLogger<TherapySettingsRepository>.Instance);
        await repository.BulkUpsertByLegacyIdAsync(
            [new TherapySettings { LegacyId = "d2:Retimed", ProfileName = "Retimed", Units = "mmol", Timestamp = RepairRawMmolProfilesFixture.Seeded.AddDays(1) }],
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
            NullLogger<ProfileDecomposer>.Instance);
    }

    private static Profile MgdlProfile(string id, string name, double isf) => new()
    {
        Id = id,
        Mills = new DateTimeOffset(RepairRawMmolProfilesFixture.Seeded.AddDays(2), TimeSpan.Zero).ToUnixTimeMilliseconds(),
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
        var contexts = Contexts(RepairRawMmolProfilesFixture.TenantA);
        var detector = new RawMmolProfileDetector(contexts);
        (await detector.DismissIsfAsync(store.SensitivityId)).Should().BeTrue();
        (await detector.DetectAsync()).Should().NotContain(i => i.Id == store.SensitivityId);
        (await DismissKeyOfAsync(contexts, "d3:Upload")).Should().NotBeNull();

        await DecomposerOver(contexts).Invoking(d => d.DecomposeAsync(MgdlProfile("d3", "Upload", 3.0), WriteOrigin.Live))
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
        var contexts = Contexts(RepairRawMmolProfilesFixture.TenantA);
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
        var contexts = Contexts(RepairRawMmolProfilesFixture.TenantA);
        var detector = new RawMmolProfileDetector(contexts);
        (await detector.DetectAsync()).Should().ContainSingle(i => i.Id == store.SensitivityId);

        await DecomposerOver(contexts).DecomposeAsync(MgdlProfile("d5", "Reupload", 216), WriteOrigin.Live);

        (await detector.DetectAsync()).Should().NotContain(i => i.Id == store.SensitivityId || i.Id == store.TargetId);
    }

    [Fact]
    public async Task DismissingAScheduleThatDoesNotExist_ReportsNotFound() =>
        (await Detector(RepairRawMmolProfilesFixture.TenantA).DismissIsfAsync(Guid.CreateVersion7())).Should().BeFalse();

    [Fact]
    public async Task OnlyLiveSchedulesAreListed_NeverAnActivationSnapshot()
    {
        var schedules = RepairRawMmolProfilesTests.Stores.SelectMany(s => new[] { s.TargetId, s.SensitivityId }).ToHashSet();

        var listed = await DetectAsync(RepairRawMmolProfilesFixture.TenantA);

        listed.Should().NotBeEmpty();
        listed.Select(i => i.Id).Should().OnlyContain(id => schedules.Contains(id));
        listed.Select(i => i.Id).Should().NotIntersectWith(RepairRawMmolProfilesTests.Activations.Select(a => a.Id));
    }

    [Fact]
    public async Task EachTenantSeesOnlyItsOwnSchedules()
    {
        var other = StoreOf("other tenant, no isf");

        var listedA = await DetectAsync(RepairRawMmolProfilesFixture.TenantA);
        var listedB = await DetectAsync(RepairRawMmolProfilesFixture.TenantB);

        listedA.Should().NotContain(i => i.Id == other.TargetId);
        listedB.Select(i => i.Id).Should().Equal(other.TargetId);
    }

    private sealed class OptionsFactory(DbContextOptions<NocturneDbContext> options) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }
}
