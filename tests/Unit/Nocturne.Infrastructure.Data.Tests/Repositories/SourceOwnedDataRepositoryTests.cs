using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Connectors;
using Nocturne.Core.Models.Services;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="SourceOwnedDataRepository"/> over SQLite, across a time series (boluses), state spans
/// and the profile tables: the discovery buckets, the counts by source, the treatment edges, and the
/// purge's per-type counts, profile names and re-import release. The tenant pin is the Postgres
/// tests' concern.
/// </summary>
[Trait("Category", "Unit")]
public class SourceOwnedDataRepositoryTests : IDisposable
{
    private const string Source = "loop-connector";
    private const string Other = "xdrip";

    private static readonly DateTime Now = DateTime.UtcNow;

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;
    private readonly List<IReadOnlyCollection<Type>> _reports = [];
    private readonly Mock<ILegacyReadCacheInvalidation> _legacyReads = new();

    public SourceOwnedDataRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, SqliteNpgsqlJson.Translate);
        _legacyReads
            .Setup(r => r.WrittenAsync(It.IsAny<IReadOnlyCollection<Type>>()))
            .Callback((IReadOnlyCollection<Type> models) => _reports.Add(models))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _db.Dispose();

    private SourceOwnedDataRepository Repository() =>
        new(new TestNocturneDbContextFactory(_db.CreateContext(_tenant)), new UserAuditContext(), _legacyReads.Object);

    private NocturneDbContext Context() => _db.CreateContext(_tenant);

    private void SeedBolus(string? dataSource, string? device, DateTime at, string legacyId)
    {
        using var db = Context();
        db.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _tenant, LegacyId = legacyId,
            DataSource = dataSource, Device = device, Timestamp = at, Insulin = 1,
        });
        db.SaveChanges();
    }

    private void SeedGlucoseTombstones(string source)
    {
        using var db = Context();
        var sensor = new SensorGlucoseEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = source, Timestamp = Now, Mgdl = 100,
            DeletedAt = Now,
        };
        var meter = new MeterGlucoseEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = source, Timestamp = Now, Mgdl = 100,
            DeletedAt = Now,
        };
        var calibration = new CalibrationEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = source, Timestamp = Now,
            DeletedAt = Now,
        };
        db.SensorGlucose.Add(sensor);
        db.MeterGlucose.Add(meter);
        db.Calibrations.Add(calibration);
        foreach (var tombstone in new object[] { sensor, meter, calibration })
            db.Entry(tombstone).Property("DeletedByUser").CurrentValue = true;
        db.SaveChanges();
    }

    private void SeedSpan(string source, DateTime at)
    {
        using var db = Context();
        db.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _tenant, Source = source,
            Category = "PumpMode", State = "Automatic", StartTimestamp = at,
        });
        db.SaveChanges();
    }

    private void SeedProfile(string source, string profileName)
    {
        using var db = Context();
        db.TherapySettings.Add(new TherapySettingsEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = source, Timestamp = Now,
            ProfileName = profileName,
        });
        db.BasalSchedules.Add(new BasalScheduleEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = source, Timestamp = Now,
            ProfileName = profileName,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task BucketsGroupEachTableInsideTheWindowByItsSourceKey()
    {
        SeedBolus(Source, "rig", Now.AddHours(-1), "recent");
        SeedBolus(Source, "rig", Now.AddDays(-3), "older");
        SeedBolus(null, Other, Now.AddHours(-2), "device-only");
        SeedBolus(Source, "rig", Now.AddDays(-40), "outside");
        SeedSpan(Source, Now.AddHours(-1));

        var buckets = await Repository().BucketsAsync(Now.AddDays(-30), Now.AddHours(-24), CancellationToken.None);

        var bolus = buckets.Single(b => b.DataType == SourceDataTypes.Boluses && b.Key == Source);
        bolus.Count.Should().Be(2);
        bolus.CountLast24Hours.Should().Be(1);
        bolus.Latest.Should().BeCloseTo(Now.AddHours(-1), TimeSpan.FromMilliseconds(1));
        bolus.Oldest.Should().BeCloseTo(Now.AddDays(-3), TimeSpan.FromMilliseconds(1));
        bolus.Handle.Should().Be(SourceHandle.DataSource);
        buckets.Single(b => b.DataType == SourceDataTypes.Boluses && b.Key == Other).Handle
            .Should().Be(SourceHandle.Device);
        var span = buckets.Single(b => b.DataType == SourceDataTypes.StateSpans);
        span.Key.Should().Be(Source);
        span.Handle.Should().BeNull("a state span's Source names neither handle in particular");
    }

    [Fact]
    public async Task CountsBySourceSumEveryTableOfAType()
    {
        SeedBolus(Source, null, Now, "a");
        SeedBolus(null, Source, Now, "b");
        SeedBolus(Other, null, Now, "c");
        SeedSpan(Source, Now);
        SeedProfile(Source, "Day");

        var counts = await Repository().CountsBySourceAsync(Source, CancellationToken.None);

        counts.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [SourceDataTypes.Boluses] = 2,
            [SourceDataTypes.StateSpans] = 1,
            [SourceDataTypes.Profiles] = 2,
        });
    }

    [Fact]
    public async Task TheTreatmentEdgesAreTheSourcesLatestAndOldestRows()
    {
        SeedBolus(Source, null, Now.AddDays(-5), "old");
        SeedBolus(Source, null, Now.AddHours(-1), "new");
        SeedBolus(Other, null, Now, "other");

        var repository = Repository();

        (await repository.LatestTreatmentTimestampAsync(Source, CancellationToken.None))
            .Should().BeCloseTo(Now.AddHours(-1), TimeSpan.FromMilliseconds(1));
        (await repository.OldestTreatmentTimestampAsync(Source, CancellationToken.None))
            .Should().BeCloseTo(Now.AddDays(-5), TimeSpan.FromMilliseconds(1));
        (await repository.LatestTreatmentTimestampAsync("nobody", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task StatsBreakDownTheSourceByTypeWithGlucoseAndSpanEdges()
    {
        SeedBolus(Source, null, Now.AddHours(-1), "recent");
        SeedBolus(Source, null, Now.AddDays(-2), "older");
        SeedSpan(Source, Now.AddDays(-2));

        var stats = await Repository().StatsBySourceAsync(Source, Now.AddHours(-24), CancellationToken.None);

        stats.Single(s => s.DataType == SourceDataTypes.Boluses).Should().Be(
            new SourceTypeStats(SourceDataTypes.Boluses, 2, 1));
        var spans = stats.Single(s => s.DataType == SourceDataTypes.StateSpans);
        spans.Count.Should().Be(1);
        spans.CountLast24Hours.Should().Be(0);
        spans.Latest.Should().BeCloseTo(Now.AddDays(-2), TimeSpan.FromMilliseconds(1));
        stats.Single(s => s.DataType == SourceDataTypes.Glucose).Count.Should().Be(0);
    }

    [Fact]
    public async Task ThePurgeSoftDeletesTheSourcesRowsCountsThemByTypeAndNamesTheProfiles()
    {
        SeedBolus(Source, null, Now, "mine");
        SeedBolus(Other, null, Now, "theirs");
        SeedSpan(Source, Now);
        SeedProfile(Source, "Day");
        SeedProfile(Other, "Night");

        var result = await Repository().SoftDeleteBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);

        result.DeletedCounts.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [SourceDataTypes.Boluses] = 1,
            [SourceDataTypes.StateSpans] = 1,
            [SourceDataTypes.Profiles] = 2,
        });
        result.ProfileNames.Should().Equal("Day");

        await using var db = Context();
        var mine = await db.Boluses.IgnoreQueryFilters().Where(b => b.LegacyId == "mine")
            .Select(b => new { b.DeletedAt, ByUser = EF.Property<bool>(b, "DeletedByUser") })
            .SingleAsync();
        mine.DeletedAt.Should().NotBeNull();
        mine.ByUser.Should().BeTrue();
        (await db.Boluses.SingleAsync()).LegacyId.Should().Be("theirs");
        (await db.TherapySettings.SingleAsync()).ProfileName.Should().Be("Night");
        (await db.MutationAuditLog.Where(a => a.Action == "bulk_delete").Select(a => a.EntityType).ToListAsync())
            .Should().Contain(["Bolus", "StateSpan"]);
    }

    [Fact]
    public async Task TheReleaseClearsTheSourcesTombstonesAndLeavesTheRowsDeleted()
    {
        SeedBolus(Source, null, Now, "mine");
        SeedBolus(Other, null, Now, "theirs");
        var repository = Repository();
        await repository.SoftDeleteBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);
        await repository.SoftDeleteBySourceAsync(Other, $"data_source={Other}", CancellationToken.None);

        await repository.AllowReimportBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);

        await using var db = Context();
        var rows = await db.Boluses.IgnoreQueryFilters()
            .Select(b => new { b.LegacyId, b.DeletedAt, ByUser = EF.Property<bool>(b, "DeletedByUser") })
            .ToListAsync();
        rows.Should().OnlyContain(r => r.DeletedAt != null);
        rows.Single(r => r.LegacyId == "mine").ByUser.Should().BeFalse();
        rows.Single(r => r.LegacyId == "theirs").ByUser.Should().BeTrue();
        (await db.MutationAuditLog.CountAsync(a => a.Action == "allow_reimport")).Should().Be(1);
    }

    [Fact]
    public async Task ThePurgeReportsTheModelOfEveryTableItDeletedFromInOneCall()
    {
        SeedBolus(Source, null, Now, "mine");
        SeedSpan(Source, Now);
        SeedProfile(Source, "Day");
        SeedBolus(Other, null, Now, "theirs");

        await Repository().SoftDeleteBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);

        _reports.Should().ContainSingle().Which.Should()
            .BeEquivalentTo([typeof(Bolus), typeof(StateSpan), typeof(TherapySettings), typeof(BasalSchedule)]);
    }

    [Fact]
    public async Task ThePurgeOfASourceThatOwnsNothingReportsNothing()
    {
        SeedBolus(Other, null, Now, "theirs");

        await Repository().SoftDeleteBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);

        _reports.Should().BeEmpty();
    }

    [Fact]
    public async Task TheReleaseReportsTheModelOfEveryTableItReleasedInOneCall()
    {
        SeedBolus(Source, null, Now, "mine");
        SeedSpan(Source, Now);
        var repository = Repository();
        await repository.SoftDeleteBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);
        _reports.Clear();

        await repository.AllowReimportBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);

        _reports.Should().ContainSingle().Which.Should().BeEquivalentTo([typeof(Bolus), typeof(StateSpan)]);
    }

    [Fact]
    public async Task TheReleaseReportsTheGlucoseTablesItReleased()
    {
        SeedGlucoseTombstones(Source);

        await Repository().AllowReimportBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);

        _reports.Should().ContainSingle().Which.Should()
            .BeEquivalentTo([typeof(SensorGlucose), typeof(MeterGlucose), typeof(Calibration)]);
    }

    [Fact]
    public async Task TheReleaseOfASourceWithNoTombstonesReportsNothing()
    {
        SeedBolus(Source, null, Now, "mine");

        await Repository().AllowReimportBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);

        _reports.Should().BeEmpty();
    }

    private sealed class UserAuditContext : IAuditContext
    {
        public Guid? SubjectId => Guid.Empty;
        public string? SubjectName => "tester";
        public string? AuthType => "SessionCookie";
        public string? IpAddress => "127.0.0.1";
        public Guid? TokenId => null;
        public string? TraceId => null;
        public string? Endpoint => "DELETE /api/v4/data-sources";
        public bool IsSystem => false;
    }
}
