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
using Nocturne.Core.Models.Services;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Covers <see cref="DataSourceService.GetActiveDataSourcesAsync"/>: which tables a discovered
/// entry's totals span, what an entry surfaced only from APS snapshots classifies as, and the
/// exclusivity of the non-glucose merge — a bucket of counts belongs to exactly one entry, or the
/// list a consumer sums over reports the same rows twice.
/// </summary>
[Trait("Category", "Unit")]
public class DataSourceServiceDiscoveryTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const string Connector = "nightscout-connector";
    private const string RigA = "openaps://rig-a";
    private const string RigB = "openaps://rig-b";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;

    private readonly Mock<ISensorGlucoseRepository> _sensorGlucose = new();
    private readonly Mock<IMeterGlucoseRepository> _meterGlucose = new();
    private readonly Mock<ICalibrationRepository> _calibrations = new();
    private readonly Mock<IConnectorConfigurationService> _connectorConfig = new();

    public DataSourceServiceDiscoveryTests()
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

    private async Task<List<DataSourceInfo>> DiscoverAsync()
    {
        await using var ctx = NewContext();
        return await CreateService(ctx).GetActiveDataSourcesAsync();
    }

    private void Seed(Action<NocturneDbContext> seed)
    {
        using var db = NewContext();
        seed(db);
        db.SaveChanges();
    }

    private void SeedSnapshot(string? dataSource, string? device, DateTime timestamp) =>
        Seed(db => db.ApsSnapshots.Add(new ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId,
            DataSource = dataSource, Device = device, Timestamp = timestamp, AidAlgorithm = "Loop",
        }));

    private void SeedSensorGlucose(string? dataSource, string? device, DateTime timestamp) =>
        Seed(db => db.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId,
            DataSource = dataSource, Device = device, Timestamp = timestamp, Mgdl = 120,
        }));

    private void SeedStateSpan(string source, DateTime startTimestamp) =>
        Seed(db => db.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Source = source,
            Category = "PumpMode", State = "Automatic", StartTimestamp = startTimestamp,
        }));

    private void SeedBolus(string? dataSource, string? device, DateTime timestamp) =>
        Seed(db => db.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId,
            DataSource = dataSource, Device = device, Timestamp = timestamp, Insulin = 1.5,
        }));

    [Fact]
    public async Task Discovery_CountsASnapshotOnlySource()
    {
        // A rig that only ever uploaded device status: no glucose row, no treatment row.
        SeedSnapshot(null, RigA, DateTime.UtcNow);

        var sources = await DiscoverAsync();

        sources.Should().ContainSingle(s => s.DeviceId == RigA)
            .Which.TotalEntries.Should().Be(1);
    }

    [Fact]
    public async Task Discovery_CountsATempBasalOnlySource()
    {
        Seed(db => db.TempBasals.Add(new TempBasalEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId,
            DataSource = RigA, StartTimestamp = DateTime.UtcNow, Rate = 0.5, Origin = "pump",
        }));

        var sources = await DiscoverAsync();

        sources.Should().ContainSingle(s => s.DeviceId == RigA)
            .Which.TotalEntries.Should().Be(1);
    }

    [Fact]
    public async Task Discovery_CountsABolusCalculationOnlySource()
    {
        Seed(db => db.BolusCalculations.Add(new BolusCalculationEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId,
            DataSource = RigA, Timestamp = DateTime.UtcNow,
        }));

        var sources = await DiscoverAsync();

        sources.Should().ContainSingle(s => s.DeviceId == RigA)
            .Which.TotalEntries.Should().Be(1);
    }

    [Fact]
    public async Task Discovery_ClassifiesASnapshotOnlyEntryFromItsDataSource()
    {
        // A device string no uploader heuristic recognises: the row's DataSource is the only handle
        // that can classify it, and device-status discovery projected none.
        SeedSnapshot(DataSources.DemoService, "pump-42", DateTime.UtcNow);

        var sources = await DiscoverAsync();

        var info = sources.Should().ContainSingle(s => s.DeviceId == "pump-42").Subject;
        info.Category.Should().Be("demo");
        info.SourceType.Should().Be("demo");
    }

    [Fact]
    public async Task Discovery_NamesTheHandleTheEntryWasFoundUnder()
    {
        // RigA is discovered from the glucose rows' Device field; the connector entry exists only
        // because a treatment bucket carries its DataSource.
        SeedSensorGlucose(null, RigA, DateTime.UtcNow);
        SeedBolus(Connector, null, DateTime.UtcNow);
        SeedBolus(null, RigB, DateTime.UtcNow);

        var sources = await DiscoverAsync();

        sources.Should().ContainSingle(s => s.DeviceId == RigA)
            .Which.DeviceIdHandle.Should().Be(SourceHandle.Device);
        sources.Should().ContainSingle(s => s.DeviceId == Connector)
            .Which.DeviceIdHandle.Should().Be(SourceHandle.DataSource);
        sources.Should().ContainSingle(s => s.DeviceId == RigB)
            .Which.DeviceIdHandle.Should().Be(SourceHandle.Device);
    }

    [Fact]
    public async Task Discovery_DoesNotLetAStateSpanPromoteABucketsHandle()
    {
        // DeviceStatusDecomposer populates StateSpan.Source from the reported device string, so a
        // span alongside a device-attributed treatment says nothing about which handle the key is.
        Seed(db => db.DeviceEvents.Add(new DeviceEventEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId,
            Device = RigA, Timestamp = DateTime.UtcNow, EventType = "SiteChange",
        }));
        SeedStateSpan(RigA, DateTime.UtcNow);

        var sources = await DiscoverAsync();

        sources.Should().ContainSingle(s => s.DeviceId == RigA)
            .Which.DeviceIdHandle.Should().Be(SourceHandle.Device);
    }

    [Fact]
    public async Task Discovery_LetsARealDataSourceColumnOutrankEarlierDeviceEvidence()
    {
        // MeterGlucose merges before Boluses, so Device evidence arrives first; the bucket's
        // handle must still resolve to the DataSource a later table proves.
        Seed(db => db.MeterGlucose.Add(new MeterGlucoseEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId,
            Device = RigA, Timestamp = DateTime.UtcNow, Mgdl = 100,
        }));
        Seed(db => db.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId,
            DataSource = RigA, Timestamp = DateTime.UtcNow, Insulin = 1.0,
        }));

        var sources = await DiscoverAsync();

        sources.Should().ContainSingle(s => s.DeviceId == RigA)
            .Which.DeviceIdHandle.Should().Be(SourceHandle.DataSource);
    }

    [Fact]
    public async Task Discovery_CallsAStateSpanOnlySourcesHandleUnknown()
    {
        // Nothing but a span: its Source may be either handle and no other table can break the tie.
        SeedStateSpan(RigA, DateTime.UtcNow);

        var sources = await DiscoverAsync();

        var info = sources.Should().ContainSingle(s => s.DeviceId == RigA).Subject;
        info.DeviceIdHandle.Should().Be(SourceHandle.Unknown);
        info.TotalEntries.Should().Be(1);
    }

    [Fact]
    public async Task Discovery_AttributesASharedBucketToTheSameSiblingEveryTime()
    {
        // Two rigs importing under one connector contend for its single bucket; the winner must not
        // depend on the order the grouping returned.
        SeedSensorGlucose(Connector, RigA, DateTime.UtcNow);
        SeedSensorGlucose(Connector, RigB, DateTime.UtcNow);
        SeedBolus(Connector, null, DateTime.UtcNow);

        var first = await DiscoverAsync();
        var again = await DiscoverAsync();

        first.Should().ContainSingle(s => s.DeviceId == RigA).Which.TotalEntries.Should().Be(2);
        again.Select(s => (s.DeviceId, s.TotalEntries))
            .Should().BeEquivalentTo(first.Select(s => (s.DeviceId, s.TotalEntries)));
    }

    [Fact]
    public async Task Discovery_CountsASharedBucketOnceAcrossTheList()
    {
        // Two rigs whose rows were imported by the same connector: both entries resolve their merge
        // key to the connector's single non-glucose bucket.
        SeedSensorGlucose(Connector, RigA, DateTime.UtcNow);
        SeedSensorGlucose(Connector, RigB, DateTime.UtcNow);
        SeedBolus(Connector, null, DateTime.UtcNow);
        SeedBolus(Connector, null, DateTime.UtcNow);

        var sources = await DiscoverAsync();

        sources.Sum(s => s.TotalEntries).Should().Be(4);
        sources.Sum(s => s.EntriesLast24Hours).Should().Be(4);
    }

    [Fact]
    public async Task Discovery_GivesABucketToTheEntryItsOwnKeyNames()
    {
        // The bucket key "openaps://rig-a" is one entry's own merge key and another entry's Device
        // field. It belongs to the former — the entry the leftover-surfacing loop would build for it.
        SeedSensorGlucose(Connector, RigA, DateTime.UtcNow);
        SeedSensorGlucose(RigA, RigB, DateTime.UtcNow);
        SeedBolus(RigA, null, DateTime.UtcNow);

        var sources = await DiscoverAsync();

        sources.Sum(s => s.TotalEntries).Should().Be(3);
        sources.Should().ContainSingle(s => s.DeviceId == RigA).Which.TotalEntries.Should().Be(1);
        sources.Should().ContainSingle(s => s.DeviceId == RigB).Which.TotalEntries.Should().Be(2);
    }

    [Fact]
    public async Task Discovery_CountsABucketOnceWhenADeviceStatusEntryRepeatsIt()
    {
        // The connector's bucket is claimed by the rig entry it imported for; the device-status
        // entry the connector's own snapshot creates must not add the same bucket again.
        SeedSensorGlucose(Connector, RigA, DateTime.UtcNow);
        SeedBolus(Connector, null, DateTime.UtcNow);
        SeedSnapshot(null, Connector, DateTime.UtcNow);

        var sources = await DiscoverAsync();

        sources.Sum(s => s.TotalEntries).Should().Be(3);
    }
}
