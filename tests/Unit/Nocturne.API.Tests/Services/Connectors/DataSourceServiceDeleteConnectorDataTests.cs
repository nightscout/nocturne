using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Health.GoogleHealth;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Health.GoogleHealth;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
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
        _ = typeof(GoogleHealthConnectorConfiguration);
        _ = typeof(GlookoConnectorConfiguration);
        _deviceId = ConnectorMetadataService.GetByConnectorId(ConnectorId)?.DataSourceId
            ?? throw new InvalidOperationException("Nightscout connector metadata failed to load");

        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, SqliteNpgsqlJson.Translate);
    }

    public void Dispose() => _db.Dispose();

    private NocturneDbContext NewContext() => _db.CreateContext(TenantId);

    private DataSourceService CreateService(NocturneDbContext context, params IConnectorPurgeGuard[] purgeGuards) =>
        CreateService(context, [], purgeGuards);

    private DataSourceService CreateService(
        NocturneDbContext context,
        IEnumerable<IConnectorDataPurge> dataPurges,
        params IConnectorPurgeGuard[] purgeGuards) => new(
        context.AsFactory(),
        _sensorGlucose.Object,
        _meterGlucose.Object,
        _calibrations.Object,
        _auditContext,
        _connectorConfig.Object,
        Nocturne.API.Tests.Infrastructure.ProfileActivationTestDeps.Writer(context),
        purgeGuards,
        dataPurges,
        NullLogger<DataSourceService>.Instance);

    private static string GoogleHealthDataSourceId =>
        ConnectorMetadataService.GetByConnectorId(GoogleHealthConnectorService.ConnectorName)!.DataSourceId;

    private static string GlookoDataSourceId =>
        ConnectorMetadataService.GetByConnectorId("Glooko")!.DataSourceId;

    private static Mock<IConnectorDataPurge> GoogleHealthSleepPurge()
    {
        var purge = new Mock<IConnectorDataPurge>();
        purge.SetupGet(p => p.ConnectorName).Returns(GoogleHealthConnectorService.ConnectorName);
        purge.Setup(p => p.DescribeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        purge.Setup(p => p.PurgeAsync(It.IsAny<bool>(), It.IsAny<IAuditContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, long> { ["Sleep"] = 1 });
        return purge;
    }

    private void SeedHealthRecords(string source)
    {
        using var db = NewContext();
        foreach (var dataSource in new[] { source, "manual" })
        {
            var at = DateTime.UtcNow.AddHours(-1);
            db.HeartRates.Add(new HeartRateEntity { Id = Guid.NewGuid(), Timestamp = at, Bpm = 60, DataSource = dataSource, SyncIdentifier = dataSource });
            db.StepCounts.Add(new StepCountEntity { Id = Guid.NewGuid(), Timestamp = at, Metric = 42, DataSource = dataSource, SyncIdentifier = dataSource });
            db.BodyWeights.Add(new BodyWeightEntity { Id = Guid.NewGuid(), Mills = new DateTimeOffset(at).ToUnixTimeMilliseconds(), WeightKg = 70, DataSource = dataSource, SyncIdentifier = dataSource });
        }

        db.SaveChanges();
    }

    private async Task AssertHealthRecordsDeletedByUserAsync(string source)
    {
        await using var db = NewContext();
        (await db.HeartRates.Select(r => r.DataSource).ToListAsync()).Should().Equal("manual");
        (await db.StepCounts.Select(r => r.DataSource).ToListAsync()).Should().Equal("manual");
        (await db.BodyWeights.Select(r => r.DataSource).ToListAsync()).Should().Equal("manual");
        (await db.UserTombstones<HeartRateEntity>().Select(r => r.DataSource).ToListAsync()).Should().Equal(source);
        (await db.UserTombstones<StepCountEntity>().Select(r => r.DataSource).ToListAsync()).Should().Equal(source);
        (await db.UserTombstones<BodyWeightEntity>().Select(r => r.DataSource).ToListAsync()).Should().Equal(source);
    }

    private static GoogleHealthPurgeGuard GoogleHealthGuard(GoogleHealthCoordinator coordinator, TimeSpan? gateTimeout = null) =>
        new(coordinator, Mock.Of<ITenantAccessor>(tenant => tenant.TenantId == TenantId), gateTimeout);

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
    public async Task DeleteConnectorData_DeletesTheSourcesProfileActivationsOnly()
    {
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
            (await CreateService(ctx).DeleteConnectorDataAsync(ConnectorId))
                .Success.Should().BeTrue();
        }

        await using var db = NewContext();
        (await db.ProfileActivations.IgnoreQueryFilters().SingleAsync(a => a.LegacyId == "ps-1")).DeletedAt.Should().NotBeNull();
        (await db.ProfileActivations.IgnoreQueryFilters().SingleAsync(a => a.LegacyId == "ps-2")).DeletedAt.Should().BeNull();
    }

    private void SeedProfileRows(string source, string profileName, bool userDeleted = false)
    {
        using var db = NewContext();
        var deletedAt = userDeleted ? DateTime.UtcNow : (DateTime?)null;
        T Row<T>() where T : V4TimeSeriesEntityBase, new() => new()
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = source,
            Timestamp = DateTime.UtcNow, DeletedAt = deletedAt,
        };

        var therapy = Row<TherapySettingsEntity>(); therapy.ProfileName = profileName;
        var basal = Row<BasalScheduleEntity>(); basal.ProfileName = profileName;
        var carbRatio = Row<CarbRatioScheduleEntity>(); carbRatio.ProfileName = profileName;
        var sensitivity = Row<SensitivityScheduleEntity>(); sensitivity.ProfileName = profileName;
        var targetRange = Row<TargetRangeScheduleEntity>(); targetRange.ProfileName = profileName;
        db.TherapySettings.Add(therapy);
        db.BasalSchedules.Add(basal);
        db.CarbRatioSchedules.Add(carbRatio);
        db.SensitivitySchedules.Add(sensitivity);
        db.TargetRangeSchedules.Add(targetRange);
        db.SaveChanges();

        if (userDeleted)
        {
            db.TherapySettings.IgnoreQueryFilters().Where(e => e.DataSource == source && e.ProfileName == profileName)
                .ExecuteUpdate(MarkUserDeleted<TherapySettingsEntity>());
            db.BasalSchedules.IgnoreQueryFilters().Where(e => e.DataSource == source && e.ProfileName == profileName)
                .ExecuteUpdate(MarkUserDeleted<BasalScheduleEntity>());
            db.CarbRatioSchedules.IgnoreQueryFilters().Where(e => e.DataSource == source && e.ProfileName == profileName)
                .ExecuteUpdate(MarkUserDeleted<CarbRatioScheduleEntity>());
            db.SensitivitySchedules.IgnoreQueryFilters().Where(e => e.DataSource == source && e.ProfileName == profileName)
                .ExecuteUpdate(MarkUserDeleted<SensitivityScheduleEntity>());
            db.TargetRangeSchedules.IgnoreQueryFilters().Where(e => e.DataSource == source && e.ProfileName == profileName)
                .ExecuteUpdate(MarkUserDeleted<TargetRangeScheduleEntity>());
        }
    }

    private static Action<UpdateSettersBuilder<T>> MarkUserDeleted<T>() where T : class =>
        s => s.SetProperty(e => EF.Property<bool>(e, "DeletedByUser"), true);

    private static async Task<int[]> LiveProfileRowsAsync(NocturneDbContext db, string source) =>
    [
        await db.TherapySettings.CountAsync(e => e.DataSource == source),
        await db.BasalSchedules.CountAsync(e => e.DataSource == source),
        await db.CarbRatioSchedules.CountAsync(e => e.DataSource == source),
        await db.SensitivitySchedules.CountAsync(e => e.DataSource == source),
        await db.TargetRangeSchedules.CountAsync(e => e.DataSource == source),
    ];

    private static async Task<int[]> ProfileTombstonesAsync(NocturneDbContext db, string source) =>
    [
        await db.UserTombstones<TherapySettingsEntity>().CountAsync(e => e.DataSource == source),
        await db.UserTombstones<BasalScheduleEntity>().CountAsync(e => e.DataSource == source),
        await db.UserTombstones<CarbRatioScheduleEntity>().CountAsync(e => e.DataSource == source),
        await db.UserTombstones<SensitivityScheduleEntity>().CountAsync(e => e.DataSource == source),
        await db.UserTombstones<TargetRangeScheduleEntity>().CountAsync(e => e.DataSource == source),
    ];

    [Fact]
    public async Task DeleteConnectorData_DeletesTheSourcesTherapySettingsAndSchedulesOnly()
    {
        SeedProfileRows(_deviceId, "Imported");
        SeedProfileRows("loop", "Loop");

        await using (var ctx = NewContext())
        {
            var result = await CreateService(ctx).DeleteConnectorDataAsync(ConnectorId);
            result.Success.Should().BeTrue();
            result.DeletedCounts["Profiles"].Should().Be(5);
        }

        await using var db = NewContext();
        (await LiveProfileRowsAsync(db, _deviceId)).Should().Equal(0, 0, 0, 0, 0);
        (await LiveProfileRowsAsync(db, "loop")).Should().Equal(1, 1, 1, 1, 1);
        (await ProfileTombstonesAsync(db, _deviceId)).Should().Equal(1, 1, 1, 1, 1);
    }

    [Fact]
    public async Task Summary_CountsTheSourcesTherapySettingsAndSchedulesUnderProfiles()
    {
        SeedProfileRows(_deviceId, "Imported");
        SeedProfileRows("loop", "Loop");

        await using var ctx = NewContext();
        var summary = await CreateService(ctx).GetConnectorDataSummaryAsync(ConnectorId);

        summary.RecordCounts.Should().BeEquivalentTo(new Dictionary<string, long> { ["Profiles"] = 5 });
    }

    [Fact]
    public async Task DeleteConnectorData_AllowingReimport_ReleasesTheSourcesTherapySettingsAndSchedules()
    {
        SeedProfileRows(_deviceId, "Imported");
        SeedProfileRows(_deviceId, "Earlier", userDeleted: true);
        SeedProfileRows("loop", "Loop", userDeleted: true);

        await using (var ctx = NewContext())
            (await CreateService(ctx).DeleteConnectorDataAsync(ConnectorId, allowReimport: true))
                .Success.Should().BeTrue();

        await using var db = NewContext();
        (await LiveProfileRowsAsync(db, _deviceId)).Should().Equal(0, 0, 0, 0, 0);
        (await ProfileTombstonesAsync(db, _deviceId)).Should().Equal(0, 0, 0, 0, 0);
        (await ProfileTombstonesAsync(db, "loop")).Should().Equal(1, 1, 1, 1, 1);
    }

    [Fact]
    public async Task DeleteConnectorData_RefreshesThePurgedProfilesTempBasalRates()
    {
        SeedProfileRows(_deviceId, "Imported");
        SeedProfileRows("loop", "Loop");
        var reresolver = new Mock<ITempBasalRateReresolver>();

        await using (var ctx = NewContext())
        {
            var writer = new ProfileActivationWriter(
                ProfileActivationTestDeps.Repository(ctx), TestTenantCache.Create(), reresolver.Object);
            var service = new DataSourceService(
                ctx.AsFactory(), _sensorGlucose.Object, _meterGlucose.Object, _calibrations.Object, _auditContext,
                _connectorConfig.Object, writer, [], [], NullLogger<DataSourceService>.Instance);
            (await service.DeleteConnectorDataAsync(ConnectorId)).Success.Should().BeTrue();
        }

        reresolver.Verify(r => r.AfterProfilesChangedAsync(
            It.Is<IReadOnlyCollection<string>>(names => names.SequenceEqual(new[] { "Imported" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteConnectorData_WithoutReimport_KeepsTheSourcesProfileTombstones()
    {
        SeedProfileRows(_deviceId, "Imported");

        await using (var ctx = NewContext())
            await CreateService(ctx).DeleteConnectorDataAsync(ConnectorId);

        await using var db = NewContext();
        (await ProfileTombstonesAsync(db, _deviceId)).Should().Equal(1, 1, 1, 1, 1);
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

    [Fact]
    public async Task DeleteConnectorData_GoogleHealth_DeletesOnlyAfterARunningSyncFinishes()
    {
        var coordinator = InMemoryGoogleHealthCoordinator.Create();
        var syncFinished = false;
        bool? deletedAfterSync = null;
        _sensorGlucose
            .Setup(r => r.DeleteBySourceAsync(It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(() => deletedAfterSync = syncFinished)
            .ReturnsAsync(0);
        var sync = await coordinator.AcquireAsync(TenantId, default);

        await using var ctx = NewContext();
        var purge = CreateService(ctx, GoogleHealthGuard(coordinator))
            .DeleteConnectorDataAsync(GoogleHealthConnectorService.ConnectorName);
        purge.IsCompleted.Should().BeFalse();
        syncFinished = true;
        await sync!.DisposeAsync();

        (await purge).Success.Should().BeTrue();
        deletedAfterSync.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteConnectorData_GoogleHealth_KeepsSyncsOutUntilThePurgeEnds()
    {
        var coordinator = InMemoryGoogleHealthCoordinator.Create();
        var deleting = new TaskCompletionSource();
        var finishDelete = new TaskCompletionSource();
        _sensorGlucose
            .Setup(r => r.DeleteBySourceAsync(It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                deleting.SetResult();
                await finishDelete.Task;
                return 0;
            });

        await using var ctx = NewContext();
        var purge = CreateService(ctx, GoogleHealthGuard(coordinator))
            .DeleteConnectorDataAsync(GoogleHealthConnectorService.ConnectorName);
        await deleting.Task;

        (await coordinator.AcquireAsync(TenantId, default, TimeSpan.Zero))
            .Should().BeNull("a sync takes this gate before it writes any record");

        finishDelete.SetResult();
        (await purge).Success.Should().BeTrue();
        await using var syncAfter = await coordinator.AcquireAsync(TenantId, default, TimeSpan.Zero);
        syncAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteConnectorData_GoogleHealth_WhileASyncHoldsTheGate_ReportsSyncRunningAndDeletesNothing()
    {
        var coordinator = InMemoryGoogleHealthCoordinator.Create();
        await using var sync = await coordinator.AcquireAsync(TenantId, default);
        SeedOneOfEachType();

        await using (var ctx = NewContext())
        {
            var result = await CreateService(ctx, GoogleHealthGuard(coordinator, TimeSpan.FromMilliseconds(50)))
                .DeleteConnectorDataAsync(GoogleHealthConnectorService.ConnectorName);

            result.Success.Should().BeFalse();
            result.ErrorCode.Should().Be(DataSourceDeleteError.SyncRunning);
        }

        _sensorGlucose.Verify(r => r.DeleteBySourceAsync(
            It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
        await using var assertCtx = NewContext();
        (await assertCtx.Boluses.IgnoreQueryFilters().SingleAsync(b => b.LegacyId == "bolus-1"))
            .DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task DeleteConnectorData_Glooko_RemovesItsHeartRateStepsAndWeightAndLeavesManualRows()
    {
        SeedHealthRecords(GlookoDataSourceId);

        await using (var ctx = NewContext())
        {
            var result = await CreateService(ctx).DeleteConnectorDataAsync("Glooko");

            result.Success.Should().BeTrue();
            result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("HeartRate", 1));
            result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("Steps", 1));
            result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("BodyWeight", 1));
        }

        await AssertHealthRecordsDeletedByUserAsync(GlookoDataSourceId);
        await using var db = NewContext();
        var summary = await db.MutationAuditLog.SingleAsync(a => a.EntityType == "HeartRate" && a.Action == "bulk_delete");
        summary.AuthType.Should().Be(AuthType);
        summary.ChangesJson.Should().Contain($"data_source={GlookoDataSourceId}");
    }

    [Fact]
    public async Task DeleteConnectorData_AllowingReimport_ReleasesTheSourcesHealthRecords()
    {
        SeedHealthRecords(GlookoDataSourceId);

        await using (var ctx = NewContext())
            (await CreateService(ctx).DeleteConnectorDataAsync("Glooko", allowReimport: true)).Success.Should().BeTrue();

        await using var db = NewContext();
        (await db.HeartRates.Select(r => r.DataSource).ToListAsync()).Should().Equal("manual");
        (await db.HeartRates.IgnoreQueryFilters().CountAsync()).Should().Be(2);
        (await db.UserTombstones<HeartRateEntity>().AnyAsync()).Should().BeFalse();
        (await db.UserTombstones<StepCountEntity>().AnyAsync()).Should().BeFalse();
        (await db.UserTombstones<BodyWeightEntity>().AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteConnectorData_GoogleHealth_DeletesItsHealthRecordsBySourceAndItsSleepThroughItsPurge(bool allowReimport)
    {
        SeedHealthRecords(GoogleHealthDataSourceId);
        var sleep = GoogleHealthSleepPurge();

        await using (var ctx = NewContext())
        {
            var result = await CreateService(ctx, [sleep.Object], GoogleHealthGuard(InMemoryGoogleHealthCoordinator.Create()))
                .DeleteConnectorDataAsync(GoogleHealthConnectorService.ConnectorName, allowReimport);

            result.Success.Should().BeTrue();
            result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("HeartRate", 1));
            result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("Steps", 1));
            result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("BodyWeight", 1));
            result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("Sleep", 1));
        }

        sleep.Verify(p => p.PurgeAsync(allowReimport, _auditContext, It.IsAny<CancellationToken>()), Times.Once);
        await using var db = NewContext();
        (await db.HeartRates.Select(r => r.DataSource).ToListAsync()).Should().Equal("manual");
        (await db.StepCounts.Select(r => r.DataSource).ToListAsync()).Should().Equal("manual");
        (await db.BodyWeights.Select(r => r.DataSource).ToListAsync()).Should().Equal("manual");
    }

    [Fact]
    public async Task DeleteConnectorData_AnotherConnector_LeavesGoogleHealthRecordsAlone()
    {
        SeedHealthRecords(GoogleHealthDataSourceId);
        var sleep = GoogleHealthSleepPurge();

        await using (var ctx = NewContext())
            await CreateService(ctx, [sleep.Object]).DeleteConnectorDataAsync(ConnectorId);

        sleep.Verify(p => p.PurgeAsync(It.IsAny<bool>(), It.IsAny<IAuditContext>(), It.IsAny<CancellationToken>()), Times.Never);
        await using var db = NewContext();
        (await db.HeartRates.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task DeleteDataSourceData_GoogleHealth_PurgesItsRecordsAndSleepThroughItsPurge()
    {
        SeedHealthRecords(GoogleHealthDataSourceId);
        var sleep = GoogleHealthSleepPurge();

        await using (var ctx = NewContext())
        {
            var service = CreateService(ctx, [sleep.Object], GoogleHealthGuard(InMemoryGoogleHealthCoordinator.Create()));
            (await service.GetActiveDataSourcesAsync()).Should().Contain(s => s.DeviceId == GoogleHealthDataSourceId);

            var result = await service.DeleteDataSourceDataAsync(GoogleHealthDataSourceId);

            result.Success.Should().BeTrue();
            result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("Sleep", 1));
        }

        await AssertHealthRecordsDeletedByUserAsync(GoogleHealthDataSourceId);
    }

    [Fact]
    public async Task DeleteDataSourceData_GoogleHealth_WhileASyncHoldsTheGate_ReportsSyncRunningAndDeletesNothing()
    {
        var coordinator = InMemoryGoogleHealthCoordinator.Create();
        await using var sync = await coordinator.AcquireAsync(TenantId, default);
        SeedHealthRecords(GoogleHealthDataSourceId);
        var sleep = GoogleHealthSleepPurge();

        await using (var ctx = NewContext())
        {
            var result = await CreateService(ctx, [sleep.Object], GoogleHealthGuard(coordinator, TimeSpan.FromMilliseconds(50)))
                .DeleteDataSourceDataAsync(GoogleHealthDataSourceId);

            result.Success.Should().BeFalse();
            result.ErrorCode.Should().Be(DataSourceDeleteError.SyncRunning);
        }

        sleep.Verify(p => p.PurgeAsync(It.IsAny<bool>(), It.IsAny<IAuditContext>(), It.IsAny<CancellationToken>()), Times.Never);
        await using var db = NewContext();
        (await db.HeartRates.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task DeleteDataSourceData_OfAConnectorWithoutAGuard_DisablesItBeforeDeleting()
    {
        SeedOneOfEachType();
        var steps = new List<string>();
        _connectorConfig
            .Setup(c => c.SetActiveAsync("Nightscout", false, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => steps.Add("disable"))
            .Returns(Task.CompletedTask);
        _sensorGlucose
            .Setup(r => r.DeleteBySourceAsync(_deviceId, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback(() => steps.Add("delete"))
            .ReturnsAsync(0);

        await using (var ctx = NewContext())
            (await CreateService(ctx).DeleteDataSourceDataAsync(_deviceId)).Success.Should().BeTrue();

        steps.Should().Equal("disable", "delete");
    }

    [Fact]
    public async Task DeleteConnectorData_GoogleHealth_DoesNotTouchThePersistedActiveFlag()
    {
        await using var ctx = NewContext();
        (await CreateService(ctx, GoogleHealthGuard(InMemoryGoogleHealthCoordinator.Create()))
            .DeleteConnectorDataAsync(GoogleHealthConnectorService.ConnectorName, allowReimport: true))
            .Success.Should().BeTrue();

        _connectorConfig.Verify(c => c.SetActiveAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteConnectorData_AnotherConnectorsGuard_StillDisablesThisConnector()
    {
        await using (var ctx = NewContext())
            await CreateService(ctx, GoogleHealthGuard(InMemoryGoogleHealthCoordinator.Create()))
                .DeleteConnectorDataAsync(ConnectorId);

        _connectorConfig.Verify(c => c.SetActiveAsync(
            ConnectorId, false, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
