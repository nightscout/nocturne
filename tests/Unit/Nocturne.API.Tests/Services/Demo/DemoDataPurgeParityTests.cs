using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V4.Admin;
using Nocturne.API.Services.Connectors;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Demo;

/// <summary>
/// The demo purge has two callers — <see cref="DemoAdminController"/>'s entries/treatments deletes
/// and <see cref="DataSourceService.DeleteDemoDataAsync"/> — and they used to hand-copy the type
/// list, which drifted. Both now route through the one owner, so each has to empty exactly the same
/// set of tables and account for the same rows.
/// </summary>
[Trait("Category", "Unit")]
public class DemoDataPurgeParityTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>Number of rows <see cref="SeedOneDemoRowOfEveryType"/> writes.</summary>
    private const int SeededRows = 12;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;

    private readonly Mock<ISensorGlucoseRepository> _sensorGlucose = new();
    private readonly Mock<IMeterGlucoseRepository> _meterGlucose = new();
    private readonly Mock<ICalibrationRepository> _calibrations = new();
    private readonly Mock<IConnectorConfigurationService> _connectorConfig = new();

    public DemoDataPurgeParityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Tenants.Add(new TenantEntity { Id = TenantId, Slug = "demo", IsDemo = true });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private NocturneDbContext NewContext() => new(_dbOptions) { TenantId = TenantId };

    private sealed class ContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }

    private DemoAdminController CreateController() =>
        new(Mock.Of<ITenantService>(), null!, new ContextFactory(_dbOptions));

    private DataSourceService CreateService(NocturneDbContext context) => new(
        context,
        _sensorGlucose.Object,
        _meterGlucose.Object,
        _calibrations.Object,
        Mock.Of<IAuditContext>(),
        _connectorConfig.Object,
        NullLogger<DataSourceService>.Instance);

    /// <summary>One demo row in every table the demo seeder and the realtime tick write to.</summary>
    private void SeedOneDemoRowOfEveryType(bool softDeleted = false)
    {
        var timestamp = DateTime.UtcNow;
        DateTime? deletedAt = softDeleted ? timestamp : null;

        using var db = NewContext();
        db.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, Mgdl = 120, DeletedAt = deletedAt,
        });
        db.MeterGlucose.Add(new MeterGlucoseEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, Mgdl = 118, DeletedAt = deletedAt,
        });
        db.Calibrations.Add(new CalibrationEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, Slope = 1, Intercept = 0, DeletedAt = deletedAt,
        });
        db.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, Insulin = 1, DeletedAt = deletedAt,
        });
        db.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, Carbs = 20, DeletedAt = deletedAt,
        });
        db.BGChecks.Add(new BGCheckEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, Glucose = 100, DeletedAt = deletedAt,
        });
        db.Notes.Add(new NoteEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, Text = "note", DeletedAt = deletedAt,
        });
        db.DeviceEvents.Add(new DeviceEventEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, EventType = "SiteChange", DeletedAt = deletedAt,
        });
        db.BolusCalculations.Add(new BolusCalculationEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, DeletedAt = deletedAt,
        });
        db.TempBasals.Add(new TempBasalEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            StartTimestamp = timestamp, Rate = 0.5, Origin = "pump", DeletedAt = deletedAt,
        });
        db.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Source = DataSources.DemoService,
            Category = "PumpMode", State = "Automatic", StartTimestamp = timestamp,
        });
        db.ApsSnapshots.Add(new ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Device = "trio-rig", Timestamp = timestamp, AidAlgorithm = "Trio", DeletedAt = deletedAt,
        });
        db.SaveChanges();
    }

    private async Task<long> RemainingDemoRowsAsync()
    {
        await using var db = NewContext();
        return await db.SensorGlucose.IgnoreQueryFilters().LongCountAsync()
            + await db.MeterGlucose.IgnoreQueryFilters().LongCountAsync()
            + await db.Calibrations.IgnoreQueryFilters().LongCountAsync()
            + await db.Boluses.IgnoreQueryFilters().LongCountAsync()
            + await db.CarbIntakes.IgnoreQueryFilters().LongCountAsync()
            + await db.BGChecks.IgnoreQueryFilters().LongCountAsync()
            + await db.Notes.IgnoreQueryFilters().LongCountAsync()
            + await db.DeviceEvents.IgnoreQueryFilters().LongCountAsync()
            + await db.BolusCalculations.IgnoreQueryFilters().LongCountAsync()
            + await db.TempBasals.IgnoreQueryFilters().LongCountAsync()
            + await db.StateSpans.IgnoreQueryFilters().LongCountAsync()
            + await db.ApsSnapshots.IgnoreQueryFilters().LongCountAsync();
    }

    private async Task<long> ControllerPurgeAsync()
    {
        var controller = CreateController();

        var entries = ((DemoDeleteResultDto)((OkObjectResult)await controller.DeleteEntries(default)).Value!).DeletedCount;
        var treatments = ((DemoDeleteResultDto)((OkObjectResult)await controller.DeleteTreatments(default)).Value!).DeletedCount;

        return entries + treatments;
    }

    private async Task<long> ServicePurgeAsync()
    {
        await using var ctx = NewContext();
        var result = await CreateService(ctx).DeleteDemoDataAsync();

        result.Success.Should().BeTrue();
        return result.TotalDeleted;
    }

    [Fact]
    public async Task BothPurgePaths_AccountForEveryDemoRowAndLeaveNothingBehind()
    {
        SeedOneDemoRowOfEveryType();
        var controllerDeleted = await ControllerPurgeAsync();
        (await RemainingDemoRowsAsync()).Should().Be(0);

        SeedOneDemoRowOfEveryType();
        var serviceDeleted = await ServicePurgeAsync();
        (await RemainingDemoRowsAsync()).Should().Be(0);

        controllerDeleted.Should().Be(SeededRows);
        serviceDeleted.Should().Be(SeededRows);
    }

    [Fact]
    public async Task BothPurgePaths_PurgeRowsAVisitorHadAlreadyDeleted()
    {
        // The demo tenant is regenerated wholesale, so a soft-deleted row has to go too; leaving it
        // behind makes it unreadable and unpurgeable.
        SeedOneDemoRowOfEveryType(softDeleted: true);

        await ControllerPurgeAsync();

        (await RemainingDemoRowsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ServicePurge_ReportsThePerTypeCountsItsCallerRenders()
    {
        SeedOneDemoRowOfEveryType();

        await using var ctx = NewContext();
        var result = await CreateService(ctx).DeleteDemoDataAsync();

        result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("Glucose", 3));
        result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("Treatments", 8));
        result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("DeviceStatus", 1));
    }
}
