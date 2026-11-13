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
/// Which tables each surface of <see cref="SourceOwnedDataRepository"/> reads or writes, and in what
/// order, with one row of the source in every V4 record table. The order is observable: the counts,
/// the purge's deleted counts and the stats reach responses as ordered maps.
/// </summary>
[Trait("Category", "Unit")]
public class SourceOwnedDataTableListTests : IDisposable
{
    private const string Source = "loop-connector";

    private static readonly DateTime Now = DateTime.UtcNow;

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;
    private readonly List<IReadOnlyCollection<Type>> _reports = [];
    private readonly Mock<ILegacyReadCacheInvalidation> _legacyReads = new();

    public SourceOwnedDataTableListTests()
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

    /// <summary>One row of <see cref="Source"/> at <c>at</c> for every V4 record table, by entity.</summary>
    private Dictionary<Type, Func<DateTime, object>> Rows() => new()
    {
        [typeof(SensorGlucoseEntity)] = at => new SensorGlucoseEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, Mgdl = 100 },
        [typeof(MeterGlucoseEntity)] = at => new MeterGlucoseEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, Mgdl = 100 },
        [typeof(CalibrationEntity)] = at => new CalibrationEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at },
        [typeof(BolusEntity)] = at => new BolusEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, Insulin = 1 },
        [typeof(CarbIntakeEntity)] = at => new CarbIntakeEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, Carbs = 10 },
        [typeof(BolusCalculationEntity)] = at => new BolusCalculationEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at },
        [typeof(DeviceEventEntity)] = at => new DeviceEventEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, EventType = "SiteChange" },
        [typeof(BGCheckEntity)] = at => new BGCheckEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, Glucose = 100 },
        [typeof(NoteEntity)] = at => new NoteEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, Text = "note" },
        [typeof(ApsSnapshotEntity)] = at => new ApsSnapshotEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, AidAlgorithm = "Loop" },
        [typeof(PumpSnapshotEntity)] = at => new PumpSnapshotEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at },
        [typeof(UploaderSnapshotEntity)] = at => new UploaderSnapshotEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at },
        [typeof(TempBasalEntity)] = at => new TempBasalEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, Rate = 0.5, Origin = "pump" },
        [typeof(BasalInjectionEntity)] = at => new BasalInjectionEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, Units = 10 },
        [typeof(StateSpanEntity)] = at => new StateSpanEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, Source = Source, StartTimestamp = at, Category = "PumpMode", State = "Automatic" },
        [typeof(ProfileActivationEntity)] = at => new ProfileActivationEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, Phase = "Requested", Origin = "Uploader", BaseProfileName = "Day" },
        [typeof(HeartRateEntity)] = at => new HeartRateEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, Bpm = 60 },
        [typeof(StepCountEntity)] = at => new StepCountEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, Metric = 10 },
        [typeof(BodyWeightEntity)] = at => new BodyWeightEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Mills = new DateTimeOffset(at).ToUnixTimeMilliseconds(), WeightKg = 70 },
        [typeof(TherapySettingsEntity)] = at => new TherapySettingsEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, ProfileName = "Day" },
        [typeof(BasalScheduleEntity)] = at => new BasalScheduleEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, ProfileName = "Day" },
        [typeof(CarbRatioScheduleEntity)] = at => new CarbRatioScheduleEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, ProfileName = "Day" },
        [typeof(SensitivityScheduleEntity)] = at => new SensitivityScheduleEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, ProfileName = "Day" },
        [typeof(TargetRangeScheduleEntity)] = at => new TargetRangeScheduleEntity { Id = Guid.CreateVersion7(), TenantId = _tenant, DataSource = Source, Timestamp = at, ProfileName = "Day" },
    };

    private static readonly Type[] TreatmentTables =
    [
        typeof(BolusEntity), typeof(CarbIntakeEntity), typeof(BGCheckEntity), typeof(NoteEntity),
        typeof(DeviceEventEntity), typeof(TempBasalEntity), typeof(BolusCalculationEntity), typeof(BasalInjectionEntity),
    ];

    public static TheoryData<Type> Treatments => [.. TreatmentTables];

    public static TheoryData<Type> NonTreatments =>
    [
        typeof(SensorGlucoseEntity), typeof(MeterGlucoseEntity), typeof(CalibrationEntity),
        typeof(ApsSnapshotEntity), typeof(PumpSnapshotEntity), typeof(UploaderSnapshotEntity),
        typeof(StateSpanEntity), typeof(ProfileActivationEntity),
        typeof(HeartRateEntity), typeof(StepCountEntity), typeof(TherapySettingsEntity),
        typeof(BasalScheduleEntity),
    ];

    private void Seed(DateTime at, bool tombstone = false, params Type[] tables)
    {
        using var db = Context();
        var rows = Rows();
        foreach (var table in tables.Length == 0 ? [.. rows.Keys] : tables)
        {
            var row = rows[table](at);
            if (tombstone)
                ((ISoftDeletable)row).DeletedAt = at;
            db.Add(row);
            if (tombstone)
                db.Entry(row).Property("DeletedByUser").CurrentValue = true;
        }

        db.SaveChanges();
    }

    [Fact]
    public async Task CountsNameEveryOwnedTableInTheirOrder()
    {
        Seed(Now);

        var counts = await Repository().CountsBySourceAsync(Source, CancellationToken.None);

        counts.Select(c => (c.Key, c.Value)).Should().Equal(
            (SourceDataTypes.Glucose, 1L),
            (SourceDataTypes.ManualBG, 1L),
            (SourceDataTypes.Calibrations, 1L),
            (SourceDataTypes.Boluses, 1L),
            (SourceDataTypes.CarbIntake, 1L),
            (SourceDataTypes.BolusCalculations, 1L),
            (SourceDataTypes.DeviceEvents, 1L),
            (SourceDataTypes.BGChecks, 1L),
            (SourceDataTypes.Notes, 1L),
            (SourceDataTypes.DeviceStatus, 3L),
            (SourceDataTypes.TempBasals, 1L),
            (SourceDataTypes.BasalInjections, 1L),
            (SourceDataTypes.StateSpans, 2L),
            (SourceDataTypes.HeartRate, 1L),
            (SourceDataTypes.Steps, 1L),
            (SourceDataTypes.BodyWeight, 1L),
            (SourceDataTypes.Profiles, 5L));
    }

    [Fact]
    public async Task BucketsListMeterGlucoseAndTheDiscoveredTablesInTheirOrder()
    {
        Seed(Now.AddHours(-1));

        var buckets = await Repository().BucketsAsync(Now.AddDays(-30), Now.AddHours(-24), CancellationToken.None);

        buckets.Select(b => b.DataType).Should().Equal(
            SourceDataTypes.ManualBG,
            SourceDataTypes.Boluses,
            SourceDataTypes.CarbIntake,
            SourceDataTypes.BolusCalculations,
            SourceDataTypes.DeviceEvents,
            SourceDataTypes.BGChecks,
            SourceDataTypes.Notes,
            SourceDataTypes.DeviceStatus,
            SourceDataTypes.TempBasals,
            SourceDataTypes.BasalInjections,
            SourceDataTypes.StateSpans,
            SourceDataTypes.HeartRate,
            SourceDataTypes.Steps,
            SourceDataTypes.BodyWeight);
        buckets.Should().OnlyContain(b => b.Key == Source && b.Count == 1);
        buckets.Single(b => b.DataType == SourceDataTypes.StateSpans).Handle.Should().BeNull();
        buckets.Where(b => b.DataType != SourceDataTypes.StateSpans)
            .Should().OnlyContain(b => b.Handle == SourceHandle.DataSource);
    }

    [Fact]
    public async Task StatsListTheirTypesInTheirOrderWithEdgesForGlucoseAndSpansOnly()
    {
        Seed(Now.AddHours(-1));

        var stats = await Repository().StatsBySourceAsync(Source, Now.AddHours(-24), CancellationToken.None);

        stats.Select(s => (s.DataType, s.Count, s.CountLast24Hours)).Should().Equal(
            (SourceDataTypes.Glucose, 1L, 1),
            (SourceDataTypes.ManualBG, 1L, 1),
            (SourceDataTypes.Boluses, 1L, 1),
            (SourceDataTypes.CarbIntake, 1L, 1),
            (SourceDataTypes.BolusCalculations, 1L, 1),
            (SourceDataTypes.Notes, 1L, 1),
            (SourceDataTypes.DeviceEvents, 1L, 1),
            (SourceDataTypes.BGChecks, 1L, 1),
            (SourceDataTypes.TempBasals, 1L, 1),
            (SourceDataTypes.StateSpans, 1L, 1),
            (SourceDataTypes.DeviceStatus, 1L, 1),
            (SourceDataTypes.BasalInjections, 1L, 1));
        stats.Where(s => s.Latest != null || s.Oldest != null).Select(s => s.DataType)
            .Should().Equal(SourceDataTypes.Glucose, SourceDataTypes.StateSpans);
    }

    [Theory]
    [MemberData(nameof(Treatments))]
    public async Task EveryTreatmentTableSetsTheTreatmentEdges(Type table)
    {
        Seed(Now.AddHours(-3), tables: table);

        var repository = Repository();

        (await repository.LatestTreatmentTimestampAsync(Source, CancellationToken.None))
            .Should().BeCloseTo(Now.AddHours(-3), TimeSpan.FromMilliseconds(1));
        (await repository.OldestTreatmentTimestampAsync(Source, CancellationToken.None))
            .Should().BeCloseTo(Now.AddHours(-3), TimeSpan.FromMilliseconds(1));
    }

    [Theory]
    [MemberData(nameof(NonTreatments))]
    public async Task NoOtherTableSetsTheTreatmentEdges(Type table)
    {
        Seed(Now.AddHours(-3), tables: table);

        (await Repository().LatestTreatmentTimestampAsync(Source, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ThePurgeDeletesEveryOwnedTableButGlucoseInTheirOrder()
    {
        Seed(Now);

        var result = await Repository().SoftDeleteBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);

        result.DeletedCounts.Select(c => (c.Key, c.Value)).Should().Equal(
            (SourceDataTypes.Boluses, 1L),
            (SourceDataTypes.CarbIntake, 1L),
            (SourceDataTypes.BolusCalculations, 1L),
            (SourceDataTypes.DeviceEvents, 1L),
            (SourceDataTypes.BGChecks, 1L),
            (SourceDataTypes.Notes, 1L),
            (SourceDataTypes.DeviceStatus, 3L),
            (SourceDataTypes.TempBasals, 1L),
            (SourceDataTypes.BasalInjections, 1L),
            (SourceDataTypes.StateSpans, 2L),
            (SourceDataTypes.HeartRate, 1L),
            (SourceDataTypes.Steps, 1L),
            (SourceDataTypes.BodyWeight, 1L),
            (SourceDataTypes.Profiles, 5L));
        _reports.Should().ContainSingle().Which.Should().BeEquivalentTo([
            typeof(Bolus), typeof(CarbIntake), typeof(BolusCalculation), typeof(DeviceEvent), typeof(BGCheck),
            typeof(Note), typeof(ApsSnapshot), typeof(PumpSnapshot), typeof(UploaderSnapshot), typeof(TempBasal),
            typeof(BasalInjection), typeof(StateSpan), typeof(ProfileActivation), typeof(HeartRate), typeof(StepCount), typeof(BodyWeight), typeof(TherapySettings),
            typeof(BasalSchedule), typeof(CarbRatioSchedule), typeof(SensitivitySchedule),
            typeof(TargetRangeSchedule),
        ]);

        await using var db = Context();
        (await db.SensorGlucose.CountAsync()).Should().Be(1);
        (await db.MeterGlucose.CountAsync()).Should().Be(1);
        (await db.Calibrations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TheReleaseCoversEveryOwnedTableGlucoseIncluded()
    {
        Seed(Now, tombstone: true);

        await Repository().AllowReimportBySourceAsync(Source, $"data_source={Source}", CancellationToken.None);

        _reports.Should().ContainSingle().Which.Should().BeEquivalentTo([
            typeof(SensorGlucose), typeof(MeterGlucose), typeof(Calibration),
            typeof(Bolus), typeof(CarbIntake), typeof(BolusCalculation), typeof(DeviceEvent), typeof(BGCheck),
            typeof(Note), typeof(ApsSnapshot), typeof(PumpSnapshot), typeof(UploaderSnapshot), typeof(TempBasal),
            typeof(BasalInjection), typeof(StateSpan), typeof(ProfileActivation), typeof(HeartRate), typeof(StepCount), typeof(BodyWeight), typeof(TherapySettings),
            typeof(BasalSchedule), typeof(CarbRatioSchedule), typeof(SensitivitySchedule),
            typeof(TargetRangeSchedule),
        ]);
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
