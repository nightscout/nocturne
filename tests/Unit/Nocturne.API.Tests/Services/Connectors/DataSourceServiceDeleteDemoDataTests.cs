using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// A demo reset has to leave the tables empty. The purge used to run under the soft-delete query
/// filter, so any record a visitor had deleted survived the reset and could then be neither read nor
/// purged.
/// </summary>
[Trait("Category", "Unit")]
public class DataSourceServiceDeleteDemoDataTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;

    private readonly Mock<ISensorGlucoseRepository> _sensorGlucose = new();
    private readonly Mock<IMeterGlucoseRepository> _meterGlucose = new();
    private readonly Mock<ICalibrationRepository> _calibrations = new();
    private readonly Mock<IConnectorConfigurationService> _connectorConfig = new();

    public DataSourceServiceDeleteDemoDataTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Tenants.Add(new TenantEntity { Id = TenantId, Slug = "demo" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private NocturneDbContext NewContext() => new(_dbOptions) { TenantId = TenantId };

    private DataSourceService CreateService(NocturneDbContext context) => new(
        context,
        _sensorGlucose.Object,
        _meterGlucose.Object,
        _calibrations.Object,
        Mock.Of<IAuditContext>(),
        _connectorConfig.Object,
        NullLogger<DataSourceService>.Instance);

    /// <summary>One already-soft-deleted demo record of every type the purge covers.</summary>
    private void SeedSoftDeletedDemoData()
    {
        var deletedAt = DateTime.UtcNow;
        var timestamp = DateTime.UtcNow;

        using var db = NewContext();
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
        db.ApsSnapshots.Add(new ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Device = DataSources.DemoService,
            Timestamp = timestamp, AidAlgorithm = "Loop", DeletedAt = deletedAt,
        });
        db.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Source = DataSources.DemoService,
            Category = "PumpMode", State = "Automatic", StartTimestamp = timestamp,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task DeleteDemoData_PurgesSoftDeletedRecords()
    {
        SeedSoftDeletedDemoData();

        await using (var ctx = NewContext())
        {
            var result = await CreateService(ctx).DeleteDemoDataAsync();

            result.Success.Should().BeTrue();
            result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("Treatments", 8));
            result.DeletedCounts.Should().Contain(new KeyValuePair<string, long>("DeviceStatus", 1));
        }

        await using var assert = NewContext();
        (await assert.Boluses.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await assert.CarbIntakes.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await assert.BGChecks.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await assert.Notes.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await assert.DeviceEvents.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await assert.BolusCalculations.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await assert.TempBasals.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await assert.ApsSnapshots.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await assert.StateSpans.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }
}
