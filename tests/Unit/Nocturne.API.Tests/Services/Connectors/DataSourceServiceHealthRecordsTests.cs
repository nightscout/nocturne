using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// A source that writes only heart rate, steps, body weight or sleep is still a source: discovery
/// lists it and the connector summary counts what a purge would delete.
/// </summary>
[Trait("Category", "Unit")]
public class DataSourceServiceHealthRecordsTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly SqliteTestDatabase _db;

    public DataSourceServiceHealthRecordsTests()
    {
        _ = typeof(GlookoConnectorConfiguration);
        _ = typeof(GoogleHealthConnectorConfiguration);

        _db = TestDbContextFactory.CreateSqlite();
        using var db = NewContext();
        db.Tenants.Add(new TenantEntity { Id = TenantId, Slug = "test" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string GoogleHealthDataSourceId =>
        ConnectorMetadataService.GetByConnectorId(GoogleHealthConnectorService.ConnectorName)!.DataSourceId;

    private static string GlookoDataSourceId => ConnectorMetadataService.GetByConnectorId("Glooko")!.DataSourceId;

    private NocturneDbContext NewContext() => _db.CreateContext(TenantId);

    private DataSourceService CreateService(NocturneDbContext context, params IConnectorDataPurge[] dataPurges) => new(
        context.AsFactory(),
        Mock.Of<ISensorGlucoseRepository>(),
        Mock.Of<IMeterGlucoseRepository>(),
        Mock.Of<ICalibrationRepository>(),
        Mock.Of<IAuditContext>(),
        Mock.Of<IConnectorConfigurationService>(),
        Nocturne.API.Tests.Infrastructure.ProfileActivationTestDeps.Writer(context),
        [],
        dataPurges,
        NullLogger<DataSourceService>.Instance);

    private void SeedHeartRate(string dataSource, DateTime at)
    {
        using var db = NewContext();
        db.HeartRates.Add(new HeartRateEntity
        {
            Id = Guid.NewGuid(), TenantId = TenantId, Timestamp = at, Bpm = 60, DataSource = dataSource,
            SyncIdentifier = Guid.NewGuid().ToString(),
        });
        db.SaveChanges();
    }

    private void SeedSteps(string dataSource, DateTime at)
    {
        using var db = NewContext();
        db.StepCounts.Add(new StepCountEntity
        {
            Id = Guid.NewGuid(), TenantId = TenantId, Timestamp = at, Metric = 42, DataSource = dataSource,
            SyncIdentifier = Guid.NewGuid().ToString(),
        });
        db.SaveChanges();
    }

    private void SeedWeight(string dataSource, DateTime at)
    {
        using var db = NewContext();
        db.BodyWeights.Add(new BodyWeightEntity
        {
            Id = Guid.NewGuid(), TenantId = TenantId, Mills = new DateTimeOffset(at).ToUnixTimeMilliseconds(),
            WeightKg = 70, DataSource = dataSource, SyncIdentifier = Guid.NewGuid().ToString(),
        });
        db.SaveChanges();
    }

    private static readonly (string Type, Action<DataSourceServiceHealthRecordsTests, string, DateTime> Seed)[] Health =
    [
        ("HeartRate", (t, source, at) => t.SeedHeartRate(source, at)),
        ("Steps", (t, source, at) => t.SeedSteps(source, at)),
        ("BodyWeight", (t, source, at) => t.SeedWeight(source, at)),
    ];

    public static TheoryData<string> HealthTypes() => new(Health.Select(h => h.Type));

    private Action<DataSourceServiceHealthRecordsTests, string, DateTime> SeedFor(string type) =>
        Health.Single(h => h.Type == type).Seed;

    private static Mock<IConnectorDataPurge> GoogleHealthSleep(DateTime start)
    {
        var purge = new Mock<IConnectorDataPurge>();
        purge.SetupGet(p => p.ConnectorName).Returns(GoogleHealthConnectorService.ConnectorName);
        purge.Setup(p => p.DescribeAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ConnectorRecordStats("Sleep", 3, 1, start, start.AddDays(-2))]);
        return purge;
    }

    [Theory]
    [MemberData(nameof(HealthTypes))]
    public async Task Discovery_ListsASourceWhoseOnlyDataIsAHealthReading(string type)
    {
        var at = DateTime.UtcNow.AddHours(-2);
        SeedFor(type)(this, GoogleHealthDataSourceId, at);
        SeedFor(type)(this, GoogleHealthDataSourceId, at.AddHours(-30));

        await using var ctx = NewContext();
        var sources = await CreateService(ctx).GetActiveDataSourcesAsync();

        var source = sources.Should().ContainSingle(s => s.DeviceId == GoogleHealthDataSourceId).Subject;
        source.TotalEntries.Should().Be(2);
        source.EntriesLast24Hours.Should().Be(1);
        source.LastSeen.Should().BeCloseTo(new DateTimeOffset(at, TimeSpan.Zero), TimeSpan.FromSeconds(1));
        source.FirstSeen.Should().BeCloseTo(new DateTimeOffset(at.AddHours(-30), TimeSpan.Zero), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Discovery_LeavesOutAHealthReadingOlderThanItsWindow()
    {
        SeedHeartRate(GoogleHealthDataSourceId, DateTime.UtcNow.AddDays(-(DataSourceService.DiscoveryWindowDays + 1)));

        await using var ctx = NewContext();
        var sources = await CreateService(ctx).GetActiveDataSourcesAsync();

        sources.Should().NotContain(s => s.DeviceId == GoogleHealthDataSourceId);
    }

    [Fact]
    public async Task Discovery_ListsASourceWhoseOnlyDataIsSleepItsConnectorOwns()
    {
        var latest = DateTime.UtcNow.AddHours(-3);

        await using var ctx = NewContext();
        var sleep = GoogleHealthSleep(latest);
        var sources = await CreateService(ctx, sleep.Object).GetActiveDataSourcesAsync();

        var source = sources.Should().ContainSingle(s => s.DeviceId == GoogleHealthDataSourceId).Subject;
        source.TotalEntries.Should().Be(3);
        source.EntriesLast24Hours.Should().Be(1);
        source.LastSeen.Should().BeCloseTo(new DateTimeOffset(latest, TimeSpan.Zero), TimeSpan.FromSeconds(1));
        var expectedSince = DateTime.UtcNow.AddDays(-DataSourceService.DiscoveryWindowDays);
        sleep.Verify(
            p => p.DescribeAsync(
                It.Is<DateTime?>(since => since.HasValue && (since.Value - expectedSince).Duration() < TimeSpan.FromMinutes(1)),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Discovery_MergesSleepIntoTheSourceThatAlsoHasHealthReadings()
    {
        SeedHeartRate(GoogleHealthDataSourceId, DateTime.UtcNow.AddHours(-5));

        await using var ctx = NewContext();
        var sources = await CreateService(ctx, GoogleHealthSleep(DateTime.UtcNow.AddHours(-3)).Object)
            .GetActiveDataSourcesAsync();

        sources.Should().ContainSingle(s => s.DeviceId == GoogleHealthDataSourceId).Which.TotalEntries.Should().Be(4);
    }

    [Fact]
    public async Task Summary_CountsAGlookoSourcesHeartRateStepsAndWeightAndNoOthers()
    {
        var at = DateTime.UtcNow.AddDays(-90);
        SeedHeartRate(GlookoDataSourceId, at);
        SeedHeartRate(GlookoDataSourceId, at);
        SeedSteps(GlookoDataSourceId, at);
        SeedWeight(GlookoDataSourceId, at);
        SeedHeartRate("manual", at);

        await using var ctx = NewContext();
        var summary = await CreateService(ctx).GetConnectorDataSummaryAsync("Glooko");

        summary.RecordCounts.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            ["HeartRate"] = 2, ["Steps"] = 1, ["BodyWeight"] = 1,
        });
    }

    [Fact]
    public async Task Summary_AddsTheSleepItsConnectorOwnsAcrossAllTime()
    {
        SeedHeartRate(GoogleHealthDataSourceId, DateTime.UtcNow.AddDays(-90));
        var sleep = GoogleHealthSleep(DateTime.UtcNow);

        await using var ctx = NewContext();
        var summary = await CreateService(ctx, sleep.Object)
            .GetConnectorDataSummaryAsync(GoogleHealthConnectorService.ConnectorName);

        summary.RecordCounts.Should().BeEquivalentTo(new Dictionary<string, long> { ["HeartRate"] = 1, ["Sleep"] = 3 });
        sleep.Verify(
            p => p.DescribeAsync(null, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Summary_CountsTheTempBasalsAPurgeDeletes()
    {
        await using (var db = NewContext())
        {
            db.TempBasals.Add(new Nocturne.Infrastructure.Data.Entities.V4.TempBasalEntity
            {
                Id = Guid.NewGuid(), TenantId = TenantId, Timestamp = DateTime.UtcNow, Rate = 1.2, Origin = "Pump",
                DataSource = GlookoDataSourceId,
            });
            await db.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var summary = await CreateService(ctx).GetConnectorDataSummaryAsync("Glooko");

        summary.RecordCounts.Should().BeEquivalentTo(new Dictionary<string, long> { ["TempBasals"] = 1 });
    }
}
