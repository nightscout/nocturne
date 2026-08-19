using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Covers <see cref="DataSourceService.GetDataSourceStatsAsync"/>'s per-type attribution. Its only
/// caller (<c>ConnectorHealthService</c>) always passes a connector's data-source id, so every type
/// must resolve a row through <c>DataSource</c> and fall back to <c>Device</c> only for the legacy
/// uploader rows that predate the column. Device status used to match on <c>Device</c> alone, which a
/// connector import never carries, so the connector's device-status count read zero.
/// </summary>
[Trait("Category", "Unit")]
public class DataSourceServiceStatsTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const string DataSource = "nightscout-connector";
    private const string Rig = "openaps://rig";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;

    private readonly Mock<ISensorGlucoseRepository> _sensorGlucose = new();
    private readonly Mock<IMeterGlucoseRepository> _meterGlucose = new();
    private readonly Mock<ICalibrationRepository> _calibrations = new();
    private readonly Mock<IConnectorConfigurationService> _connectorConfig = new();

    public DataSourceServiceStatsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Tenants.Add(new TenantEntity { Id = TenantId, Slug = "test" });
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

    private void SeedApsSnapshot(string? dataSource, string? device, DateTime timestamp)
    {
        using var db = NewContext();
        db.ApsSnapshots.Add(new ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            DataSource = dataSource,
            Device = device,
            Timestamp = timestamp,
            AidAlgorithm = "Loop",
        });
        db.SaveChanges();
    }

    private async Task<DataSourceStats> StatsFor(string dataSource)
    {
        await using var ctx = NewContext();
        return await CreateService(ctx).GetDataSourceStatsAsync(dataSource);
    }

    [Fact]
    public async Task Stats_AttributeAnImportedSnapshotToItsConnector()
    {
        // The shape DeviceStatusDecomposer writes for a connector import: Device is the rig string the
        // uploader reported, DataSource is the connector id.
        SeedApsSnapshot(DataSource, Rig, DateTime.UtcNow);

        var stats = await StatsFor(DataSource);

        stats.TypeBreakdown.Should().Contain(new KeyValuePair<string, long>("DeviceStatus", 1));
        stats.TypeBreakdownLast24Hours.Should().Contain(new KeyValuePair<string, int>("DeviceStatus", 1));
    }

    [Fact]
    public async Task Stats_ExcludeSnapshotsFromAnotherDataSource()
    {
        SeedApsSnapshot("glooko", Rig, DateTime.UtcNow);

        var stats = await StatsFor(DataSource);

        stats.TypeBreakdown.Should().NotContainKey("DeviceStatus");
    }

    [Fact]
    public async Task Stats_StillAttributeALegacyUploaderSnapshotByDevice()
    {
        // A direct v1 devicestatus upload carries no DataSource, so Device is the only handle on it.
        SeedApsSnapshot(null, DataSource, DateTime.UtcNow);

        var stats = await StatsFor(DataSource);

        stats.TypeBreakdown.Should().Contain(new KeyValuePair<string, long>("DeviceStatus", 1));
    }

    [Fact]
    public async Task Stats_CountOnlyRecentSnapshotsIn24HourBreakdown()
    {
        SeedApsSnapshot(DataSource, Rig, DateTime.UtcNow);
        SeedApsSnapshot(DataSource, Rig, DateTime.UtcNow.AddHours(-25));

        var stats = await StatsFor(DataSource);

        stats.TypeBreakdown.Should().Contain(new KeyValuePair<string, long>("DeviceStatus", 2));
        stats.TypeBreakdownLast24Hours.Should().Contain(new KeyValuePair<string, int>("DeviceStatus", 1));
    }
}
