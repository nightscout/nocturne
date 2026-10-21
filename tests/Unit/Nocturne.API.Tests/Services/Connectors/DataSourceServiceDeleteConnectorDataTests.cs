using Nocturne.API.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.V4;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Services;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Covers the consistency fix for <see cref="DataSourceService.DeleteConnectorDataAsync"/>: every
/// data type a connector wrote must be deleted through the strongest audited path it supports (so
/// auditable types are user-attributed and the soft-delete dedup blocks re-import), and the connector
/// must be disabled so a scheduled sync can't re-import. Previously treatments hard-deleted with no
/// audit and silently re-imported on the next sync.
/// </summary>
[Trait("Category", "Unit")]
public class DataSourceServiceDeleteConnectorDataTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const string ConnectorId = "nightscout";
    private const string AuthType = "OAuthAccessToken";

    private readonly SqliteTestDatabase _db;
    private readonly string _deviceId;

    private readonly Mock<ISensorGlucoseRepository> _sensorGlucose = new();
    private readonly Mock<IMeterGlucoseRepository> _meterGlucose = new();
    private readonly Mock<ICalibrationRepository> _calibrations = new();
    private readonly Mock<IConnectorConfigurationService> _connectorConfig = new();

    private readonly AuditContext _auditContext = new()
    {
        SubjectId = Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
        SubjectName = "admin@example.com",
        AuthType = AuthType,
    };

    public DataSourceServiceDeleteConnectorDataTests()
    {
        // Force-load the Nightscout connector assembly so the static metadata registry can resolve
        // the connector id to its data-source id ("nightscout-connector").
        _ = typeof(NightscoutConnectorConfiguration);
        _deviceId = ConnectorMetadataService.GetByConnectorId(ConnectorId)?.DataSourceId
            ?? throw new InvalidOperationException("Nightscout connector metadata failed to load");

        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, SqliteNpgsqlJson.Translate);
    }

    public void Dispose() => _db.Dispose();

    private NocturneDbContext NewContext() => _db.CreateContext(TenantId);

    private DataSourceService CreateService(NocturneDbContext context, ITreatmentDecomposer? decomposer = null) => new(
        context.AsFactory(),
        _sensorGlucose.Object,
        _meterGlucose.Object,
        _calibrations.Object,
        _auditContext,
        _connectorConfig.Object,
        decomposer ?? CreateDecomposer(context, Mock.Of<IProfileDecomposer>()),
        Nocturne.API.Tests.Infrastructure.ProfileActivationTestDeps.Writer(context),
        NullLogger<DataSourceService>.Instance);

    private TreatmentDecomposer CreateDecomposer(NocturneDbContext context, IProfileDecomposer profiles) => new(
        context.AsFactory(),
        Mock.Of<IBolusRepository>(), Mock.Of<ITempBasalRepository>(), Mock.Of<ICarbIntakeRepository>(),
        Mock.Of<IBGCheckRepository>(), Mock.Of<INoteRepository>(), Mock.Of<IDeviceEventRepository>(),
        Mock.Of<IBolusCalculationRepository>(), Mock.Of<IStateSpanService>(),
        ProfileActivationTestDeps.Repository(context), ProfileActivationTestDeps.Writer(context), Mock.Of<ITreatmentFoodService>(),
        Mock.Of<IDeviceService>(), Mock.Of<IPatientDeviceStamper>(), profiles, Mock.Of<IActiveProfileResolver>(),
        Mock.Of<IPatientInsulinRepository>(), _auditContext, NullLogger<TreatmentDecomposer>.Instance);

    private static Profile ProfileDocument(string id, string storeName) => new()
    {
        Id = id,
        Mills = 1672567200000,
        DefaultProfile = storeName,
        Store =
        {
            [storeName] = new ProfileData
            {
                Dia = 3.0,
                Timezone = "UTC",
                Basal = [new TimeValue { Time = "00:00", Value = 1.0 }],
                CarbRatio = [new TimeValue { Time = "00:00", Value = 10.0 }],
                Sens = [new TimeValue { Time = "00:00", Value = 50.0 }],
                TargetLow = [new TimeValue { Time = "00:00", Value = 80.0 }],
                TargetHigh = [new TimeValue { Time = "00:00", Value = 120.0 }],
            },
        },
    };

    private void SeedOneOfEachType()
    {
        using var db = NewContext();

        // Auditable + soft-deletable: audited soft-delete, user-attributed, blocks re-import.
        db.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = "bolus-1",
            DataSource = _deviceId,
            Timestamp = DateTime.UtcNow,
            Insulin = 1.5,
        });
        db.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = "carb-1",
            DataSource = _deviceId,
            Timestamp = DateTime.UtcNow,
            Carbs = 20,
        });

        db.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            Source = _deviceId,
            Category = "PumpMode",
            State = "Automatic",
            StartTimestamp = DateTime.UtcNow,
        });

        db.BGChecks.Add(new BGCheckEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = "bgcheck-1",
            DataSource = _deviceId,
            Timestamp = DateTime.UtcNow,
            Glucose = 100,
        });
        db.Notes.Add(new NoteEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = "note-1",
            DataSource = _deviceId,
            Timestamp = DateTime.UtcNow,
            Text = "hello",
        });
        // An imported snapshot's Device is the rig string the uploader reported, so only DataSource
        // ties it back to the connector.
        db.ApsSnapshots.Add(new ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = "aps-1",
            DataSource = _deviceId,
            Device = "openaps://rig",
            Timestamp = DateTime.UtcNow,
            AidAlgorithm = "Loop",
        });

        db.SaveChanges();
    }

    [Fact]
    public async Task DeleteConnectorData_RoutesEachTypeThroughItsStrongestAuditedPath()
    {
        SeedOneOfEachType();

        await using (var ctx = NewContext())
        {
            var result = await CreateService(ctx).DeleteConnectorDataAsync(ConnectorId);
            result.Success.Should().BeTrue();
            result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("Boluses", 1));
            result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("StateSpans", 1));
        }

        await using var assertCtx = NewContext();

        // Auditable treatments are soft-deleted (row retained, DeletedAt set) — not hard-deleted.
        var bolus = await assertCtx.Boluses.IgnoreQueryFilters()
            .SingleAsync(b => b.LegacyId == "bolus-1");
        bolus.DeletedAt.Should().NotBeNull();

        // ...and are covered by one user-attributed bulk_delete summary row naming the purged source.
        var bolusSummary = await assertCtx.MutationAuditLog.SingleAsync(a =>
            a.EntityType == "Bolus" && a.Action == "bulk_delete");
        bolusSummary.AuthType.Should().Be(AuthType);
        bolusSummary.EntityId.Should().BeNull();
        bolusSummary.ChangesJson.Should().Contain($"data_source={_deviceId}");

        (await assertCtx.StateSpans.IgnoreQueryFilters().SingleAsync(s => s.Source == _deviceId))
            .DeletedAt.Should().NotBeNull();

        (await assertCtx.BGChecks.IgnoreQueryFilters().SingleAsync(b => b.LegacyId == "bgcheck-1"))
            .DeletedAt.Should().NotBeNull();
        (await assertCtx.ApsSnapshots.IgnoreQueryFilters().SingleAsync(a => a.LegacyId == "aps-1"))
            .DeletedAt.Should().NotBeNull();
        (await assertCtx.MutationAuditLog.Where(a => a.Action == "bulk_delete")
            .Select(a => a.EntityType).ToListAsync())
            .Should().Contain(["BGCheck", "Note", "ApsSnapshot", "StateSpan"]);
    }

    [Fact]
    public async Task DeleteConnectorData_DeletesTheProfileRowsOfTheSourcesProfileSwitchesOnly()
    {
        const string storeName = "Weekend@@@@@1672567200000";
        await using (var seed = NewContext())
        {
            seed.ProfileActivations.AddRange(
                new ProfileActivationEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = "ps-1", DataSource = _deviceId,
                    Timestamp = DateTime.UtcNow, Phase = "Requested", Origin = "Uploader", BaseProfileName = "Weekend",
                },
                new ProfileActivationEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = "ps-2", DataSource = "loop",
                    Timestamp = DateTime.UtcNow, Phase = "Requested", Origin = "Uploader", BaseProfileName = "Weekend",
                });
            await seed.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
        {
            var factory = new TestNocturneDbContextFactory(ctx);
            var profiles = new ProfileDecomposer(
                new TherapySettingsRepository(factory, _auditContext, NullLogger<TherapySettingsRepository>.Instance),
                new BasalScheduleRepository(factory, _auditContext, NullLogger<BasalScheduleRepository>.Instance),
                new CarbRatioScheduleRepository(factory, _auditContext, NullLogger<CarbRatioScheduleRepository>.Instance),
                new SensitivityScheduleRepository(factory, _auditContext, NullLogger<SensitivityScheduleRepository>.Instance),
                new TargetRangeScheduleRepository(factory, _auditContext, NullLogger<TargetRangeScheduleRepository>.Instance),
                NullLogger<ProfileDecomposer>.Instance);
            await profiles.DecomposeAsync(ProfileDocument("ps-1", storeName), WriteOrigin.Live);
            await profiles.DecomposeAsync(ProfileDocument("ps-2", storeName), WriteOrigin.Live);

            (await CreateService(ctx, CreateDecomposer(ctx, profiles)).DeleteConnectorDataAsync(ConnectorId))
                .Success.Should().BeTrue();
        }

        await using var db = NewContext();
        var snapshots = (await db.TherapySettings.IgnoreQueryFilters().ToListAsync()).Select(r => (r.LegacyId, r.DeletedAt))
            .Concat((await db.BasalSchedules.IgnoreQueryFilters().ToListAsync()).Select(r => (r.LegacyId, r.DeletedAt)))
            .Concat((await db.CarbRatioSchedules.IgnoreQueryFilters().ToListAsync()).Select(r => (r.LegacyId, r.DeletedAt)))
            .Concat((await db.SensitivitySchedules.IgnoreQueryFilters().ToListAsync()).Select(r => (r.LegacyId, r.DeletedAt)))
            .Concat((await db.TargetRangeSchedules.IgnoreQueryFilters().ToListAsync()).Select(r => (r.LegacyId, r.DeletedAt)))
            .ToList();
        snapshots.Where(r => r.LegacyId == $"ps-1:{storeName}").Should().HaveCount(5)
            .And.AllSatisfy(r => r.DeletedAt.Should().NotBeNull());
        snapshots.Where(r => r.LegacyId == $"ps-2:{storeName}").Should().HaveCount(5)
            .And.AllSatisfy(r => r.DeletedAt.Should().BeNull());
        (await db.ProfileActivations.IgnoreQueryFilters().SingleAsync(a => a.LegacyId == "ps-1")).DeletedAt.Should().NotBeNull();
        (await db.ProfileActivations.IgnoreQueryFilters().SingleAsync(a => a.LegacyId == "ps-2")).DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task DeleteConnectorData_BlocksReimportOfEveryAuditableType()
    {
        SeedOneOfEachType();

        await using (var ctx = NewContext())
            await CreateService(ctx).DeleteConnectorDataAsync(ConnectorId);

        await using var assertCtx = NewContext();

        // An active row blocks re-import too, so each row must be shown deleted before "blocking"
        // says anything about attribution.
        (await assertCtx.Boluses.IgnoreQueryFilters().SingleAsync(b => b.LegacyId == "bolus-1")).DeletedAt.Should().NotBeNull();
        (await assertCtx.CarbIntakes.IgnoreQueryFilters().SingleAsync(c => c.LegacyId == "carb-1")).DeletedAt.Should().NotBeNull();
        (await assertCtx.BGChecks.IgnoreQueryFilters().SingleAsync(b => b.LegacyId == "bgcheck-1")).DeletedAt.Should().NotBeNull();
        (await assertCtx.Notes.IgnoreQueryFilters().SingleAsync(n => n.LegacyId == "note-1")).DeletedAt.Should().NotBeNull();
        (await assertCtx.ApsSnapshots.IgnoreQueryFilters().SingleAsync(a => a.LegacyId == "aps-1")).DeletedAt.Should().NotBeNull();

        // The dedup that guards bulk-create treats every user-deleted row as blocking, so the next
        // sync cannot re-import them.
        (await assertCtx.GetBlockingLegacyIdsAsync<BolusEntity>(["bolus-1"])).Held.Should().Contain("bolus-1");
        (await assertCtx.GetBlockingLegacyIdsAsync<CarbIntakeEntity>(["carb-1"])).Held.Should().Contain("carb-1");
        (await assertCtx.GetBlockingLegacyIdsAsync<BGCheckEntity>(["bgcheck-1"])).Held.Should().Contain("bgcheck-1");
        (await assertCtx.GetBlockingLegacyIdsAsync<NoteEntity>(["note-1"])).Held.Should().Contain("note-1");
        (await assertCtx.GetBlockingLegacyIdsAsync<ApsSnapshotEntity>(["aps-1"])).Held.Should().Contain("aps-1");
    }

    [Fact]
    public async Task DeleteConnectorData_DisablesTheConnector()
    {
        SeedOneOfEachType();

        await using (var ctx = NewContext())
            await CreateService(ctx).DeleteConnectorDataAsync(ConnectorId);

        _connectorConfig.Verify(c => c.SetActiveAsync(
            ConnectorId, false, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteConnectorData_AllowingReimport_LeavesEveryTypeReimportable()
    {
        SeedOneOfEachType();

        await using (var ctx = NewContext())
            (await CreateService(ctx).DeleteConnectorDataAsync(ConnectorId, allowReimport: true))
                .Success.Should().BeTrue();

        await using var assertCtx = NewContext();

        (await assertCtx.Boluses.IgnoreQueryFilters().SingleAsync(b => b.LegacyId == "bolus-1")).DeletedAt.Should().NotBeNull();
        (await assertCtx.ApsSnapshots.IgnoreQueryFilters().SingleAsync(a => a.LegacyId == "aps-1")).DeletedAt.Should().NotBeNull();
        (await assertCtx.StateSpans.IgnoreQueryFilters().SingleAsync(s => s.Source == _deviceId)).DeletedAt.Should().NotBeNull();

        (await assertCtx.GetBlockingLegacyIdsAsync<BolusEntity>(["bolus-1"])).Held.Should().BeEmpty();
        (await assertCtx.GetBlockingLegacyIdsAsync<CarbIntakeEntity>(["carb-1"])).Held.Should().BeEmpty();
        (await assertCtx.GetBlockingLegacyIdsAsync<BGCheckEntity>(["bgcheck-1"])).Held.Should().BeEmpty();
        (await assertCtx.GetBlockingLegacyIdsAsync<NoteEntity>(["note-1"])).Held.Should().BeEmpty();
        (await assertCtx.GetBlockingLegacyIdsAsync<ApsSnapshotEntity>(["aps-1"])).Held.Should().BeEmpty();
        (await assertCtx.UserTombstones<StateSpanEntity>().AnyAsync()).Should().BeFalse();

        var release = await assertCtx.MutationAuditLog.SingleAsync(a =>
            a.EntityType == "Bolus" && a.Action == "allow_reimport");
        release.AuthType.Should().Be(AuthType);
        release.ChangesJson.Should().Contain($"data_source={_deviceId}");
    }

    [Fact]
    public async Task DeleteConnectorData_AllowingReimport_ReleasesTheSourcesEarlierDeletesOnly()
    {
        await using (var seed = NewContext())
        {
            seed.Boluses.AddRange(
                new BolusEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = "earlier", DataSource = _deviceId,
                    Timestamp = DateTime.UtcNow, Insulin = 1, DeletedAt = DateTime.UtcNow,
                },
                new BolusEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = TenantId, LegacyId = "other-source", DataSource = "loop",
                    Timestamp = DateTime.UtcNow, Insulin = 1, DeletedAt = DateTime.UtcNow,
                });
            await seed.SaveChangesAsync();
            await seed.Boluses.IgnoreQueryFilters()
                .ExecuteUpdateAsync(s => s.SetProperty(e => EF.Property<bool>(e, "DeletedByUser"), true));
        }

        await using (var ctx = NewContext())
            await CreateService(ctx).DeleteConnectorDataAsync(ConnectorId, allowReimport: true);

        await using var assertCtx = NewContext();
        (await assertCtx.GetBlockingLegacyIdsAsync<BolusEntity>(["earlier", "other-source"])).Held
            .Should().BeEquivalentTo(["other-source"]);
    }

    [Fact]
    public async Task DeleteConnectorData_AllowingReimport_ReenablesAnEnabledConnector()
    {
        _connectorConfig
            .Setup(c => c.GetConfigurationAsync(ConnectorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse { ConnectorName = ConnectorId, IsActive = true });

        await using (var ctx = NewContext())
            await CreateService(ctx).DeleteConnectorDataAsync(ConnectorId, allowReimport: true);

        _connectorConfig.Verify(c => c.SetActiveAsync(
            ConnectorId, false, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _connectorConfig.Verify(c => c.SetActiveAsync(
            ConnectorId, true, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteConnectorData_AllowingReimport_LeavesADisabledConnectorDisabled()
    {
        _connectorConfig
            .Setup(c => c.GetConfigurationAsync(ConnectorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectorConfigurationResponse { ConnectorName = ConnectorId, IsActive = false });

        await using (var ctx = NewContext())
            await CreateService(ctx).DeleteConnectorDataAsync(ConnectorId, allowReimport: true);

        _connectorConfig.Verify(c => c.SetActiveAsync(
            ConnectorId, true, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteConnectorData_UnknownConnector_ReturnsFailureWithoutDisabling()
    {
        await using var ctx = NewContext();
        var result = await CreateService(ctx).DeleteConnectorDataAsync("not-a-connector");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(
            DataSourceDeleteError.NotFound,
            "the controller maps the 404 off the error code, not the message text");
        _connectorConfig.Verify(c => c.SetActiveAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
