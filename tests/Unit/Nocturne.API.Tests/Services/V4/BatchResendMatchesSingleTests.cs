using Nocturne.API.Tests.TestDoubles;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Infrastructure.Data.Repositories;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// A legacy record re-sent with changed fields through a decomposer's batch path updates the
/// stored rows, as the same resend through the single-record path does (#1083).
/// </summary>
[Trait("Category", "Unit")]
public class BatchResendMatchesSingleTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const long At = 1_780_000_000_000;

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TestNocturneDbContextFactory _factory;
    private readonly IAuditContext _audit = Mock.Of<IAuditContext>();
    private readonly IDuplicateLinker _dedup = Mock.Of<IDuplicateLinker>();

    public BatchResendMatchesSingleTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();
        _factory = new TestNocturneDbContextFactory(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Entry_BatchResend_UpdatesSensorMeterAndCalibrationRows()
    {
        var decomposer = EntryDecomposerDoubles.Build(
            sensorGlucose: new SensorGlucoseRepository(_factory, _dedup, _audit, NullLogger<SensorGlucoseRepository>.Instance),
            meterGlucose: new MeterGlucoseRepository(_factory, _audit, NullLogger<MeterGlucoseRepository>.Instance),
            calibration: new CalibrationRepository(_factory, _audit, NullLogger<CalibrationRepository>.Instance),
            audit: _audit);

        Entry Sgv(double value) => new() { Id = "sgv-1", Type = "sgv", Mills = At, Sgv = value, Device = "cgm" };
        Entry Mbg(double value) => new() { Id = "mbg-1", Type = "mbg", Mills = At, Mbg = value, Device = "meter" };
        Entry Cal(double slope) => new() { Id = "cal-1", Type = "cal", Mills = At, Slope = slope, Intercept = 1, Scale = 1 };

        await decomposer.DecomposeAsync(Sgv(100), WriteOrigin.Live);
        await decomposer.DecomposeAsync(Mbg(110), WriteOrigin.Live);
        await decomposer.DecomposeAsync(Cal(800), WriteOrigin.Live);

        var result = await decomposer.DecomposeBatchAsync([Sgv(140), Mbg(150), Cal(900)], WriteOrigin.Live);

        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().HaveCount(3);
        _context.SensorGlucose.Select(e => e.Mgdl).Should().Equal(140);
        _context.MeterGlucose.Select(e => e.Mgdl).Should().Equal(150);
        _context.Calibrations.Select(e => e.Slope).Should().Equal(900);
    }

    [Fact]
    public async Task Treatment_BatchResend_UpdatesRowsAndDoesNotReseedAFoodLine()
    {
        var foods = new Mock<ITreatmentFoodService>();
        foods
            .Setup(s => s.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var decomposer = TreatmentDecomposerDoubles.Stored(_factory, _dedup, _audit, foods: foods.Object);

        Treatment Carbs(double carbs) => new()
        {
            Id = "6500000000000000000000c1", EventType = "Carb Correction", Mills = At,
            Carbs = carbs, FoodType = "apple",
        };
        Treatment Note(string text) => new()
        {
            Id = "6500000000000000000000n1", EventType = "Note", Mills = At, Notes = text,
        };
        Treatment TempBasal(double rate) => new()
        {
            Id = "6500000000000000000000t1", EventType = "Temp Basal", Mills = At,
            Rate = rate, Absolute = rate, Duration = 30,
        };

        await decomposer.DecomposeOneAsync(Carbs(20), WriteOrigin.Live);
        await decomposer.DecomposeOneAsync(Note("before"), WriteOrigin.Live);
        await decomposer.DecomposeOneAsync(TempBasal(0.5), WriteOrigin.Live);

        var result = await decomposer.DecomposeBatchAsync(
            [Carbs(35), Note("after"), TempBasal(1.5)], WriteOrigin.Live);

        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().HaveCount(3);
        _context.CarbIntakes.Select(e => e.Carbs).Should().Equal(35);
        _context.Notes.Select(e => e.Text).Should().Equal("after");
        _context.TempBasals.Select(e => e.Rate).Should().Equal(1.5);
        foods.Verify(
            s => s.AddManyAsync(It.Is<IReadOnlyCollection<TreatmentFood>>(lines => lines.Count == 1), It.IsAny<CancellationToken>()),
            Times.Once,
            "the food line is seeded on create only, so a resend leaves a user's deletion of it standing");
    }

    [Fact]
    public async Task DeviceStatus_BatchResend_UpdatesTheSnapshotRow()
    {
        var decomposer = DeviceStatusDecomposerDoubles.Stored(_factory, _audit);

        DeviceStatus Status(int battery) => new()
        {
            Id = "6500000000000000000000d1", Mills = At, Device = "phone", UploaderBattery = battery,
        };

        await decomposer.DecomposeBatchAsync([Status(80)], source: null, WriteOrigin.Live);

        var result = await decomposer.DecomposeBatchAsync([Status(40)], source: null, WriteOrigin.Live);

        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().ContainSingle();
        _context.UploaderSnapshots.Select(e => e.Battery).Should().Equal(40);
    }

    [Fact]
    public async Task DeviceStatus_ResendWithExtras_KeepsOneExtrasRowThatADeleteRemoves()
    {
        var decomposer = DeviceStatusDecomposerDoubles.Stored(_factory, _audit);

        const string legacyId = "6500000000000000000000d2";
        DeviceStatus Status(int battery) => new()
        {
            Id = legacyId, Mills = At, Device = "phone", UploaderBattery = battery,
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["configuration"] = JsonDocument.Parse("""{"version":1}""").RootElement,
            },
        };

        Task Send(DeviceStatus ds) => decomposer.DecomposeBatchAsync([ds], source: null, WriteOrigin.Live);

        await Send(Status(80));
        var storedCorrelationId = _context.UploaderSnapshots.Single().CorrelationId;

        await Send(Status(40));

        _context.UploaderSnapshots.Select(e => e.CorrelationId).Should().Equal(storedCorrelationId);
        _context.DeviceStatusExtras.IgnoreQueryFilters().Where(e => e.DeletedAt == null)
            .Select(e => (Guid?)e.CorrelationId).Should().Equal(storedCorrelationId);

        await decomposer.DeleteStoredAsync(legacyId, WriteOrigin.Live);

        _context.DeviceStatusExtras.IgnoreQueryFilters().Where(e => e.DeletedAt == null)
            .Should().BeEmpty("deleting the device status must reach its extras row");
    }

    [Fact]
    public async Task DeviceStatus_ResendAfterUserDelete_WritesNoExtrasRow()
    {
        var decomposer = DeviceStatusDecomposerDoubles.Stored(_factory, _audit);

        const string legacyId = "6500000000000000000000d3";
        DeviceStatus Status() => new()
        {
            Id = legacyId, Mills = At, Device = "phone", UploaderBattery = 80,
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["configuration"] = JsonDocument.Parse("""{"version":1}""").RootElement,
            },
        };

        await decomposer.DecomposeBatchAsync([Status()], source: null, WriteOrigin.Live);
        await decomposer.DeleteStoredAsync(legacyId, WriteOrigin.Live);

        var result = await decomposer.DecomposeBatchAsync([Status()], source: null, WriteOrigin.Live);

        result.CreatedRecords.Should().BeEmpty();
        _context.DeviceStatusExtras.IgnoreQueryFilters().Where(e => e.DeletedAt == null)
            .Should().BeEmpty("a resend must not revive the deleted group's extras under a fresh correlation id");
    }

    /// <summary>
    /// A status that writes no snapshot has no legacy-keyed row to keep its correlation id, so its
    /// re-send is recognised by the extras it holds: it writes no second row and reports nothing written.
    /// </summary>
    [Fact]
    public async Task DeviceStatus_SnapshotlessResend_WritesNoSecondExtrasRow()
    {
        var decomposer = DeviceStatusDecomposerDoubles.Stored(_factory, _audit);

        DeviceStatus Status() => new()
        {
            Id = "6500000000000000000000d4", Mills = At, Device = "xdrip",
            XDripJs = new XDripJsStatus { State = 6, StateString = "OK" },
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["configuration"] = JsonDocument.Parse("""{"version":1}""").RootElement,
            },
        };

        await decomposer.DecomposeBatchAsync([Status()], source: null, WriteOrigin.Live);
        var resend = await decomposer.DecomposeBatchAsync([Status()], source: null, WriteOrigin.Live);

        resend.Wrote.Should().Equal(false);
        resend.UnchangedRecords.Should().ContainSingle().Which.Should().BeOfType<Nocturne.Core.Models.V4.DeviceStatusExtras>();
        _context.DeviceStatusExtras.Should().ContainSingle();
    }

    [Fact]
    public async Task DeviceStatus_Replace_RewritesItsSensorStateReport()
    {
        // A mutable AuditContext takes the system attribution a replacement's deletes run under.
        var decomposer = DeviceStatusDecomposerDoubles.Stored(
            _factory, new AuditContext { SubjectId = Guid.CreateVersion7(), AuthType = "SessionCookie" });

        await decomposer.DecomposeBatchAsync([CareLinkStatus("WARM_UP")], "carelink-connector", WriteOrigin.Live);
        await decomposer.ReplaceAsync(CareLinkStatusId, CareLinkStatus("SENSOR_ERROR"), WriteOrigin.Live);

        LiveSensorStateReports().Should().ContainSingle().Which.State.Should().Be(SensorState.Error);
    }

    [Fact]
    public async Task DeviceStatus_DeleteThenResend_DeletesItsSensorStateReportAndWritesNoNewOne()
    {
        var decomposer = DeviceStatusDecomposerDoubles.Stored(_factory, _audit);

        await decomposer.DecomposeBatchAsync([CareLinkStatus("WARM_UP")], "carelink-connector", WriteOrigin.Live);
        await decomposer.DeleteStoredAsync(CareLinkStatusId, WriteOrigin.Live);
        LiveSensorStateReports().Should().BeEmpty("deleting the device status must reach its sensor state report");

        await decomposer.DecomposeBatchAsync([CareLinkStatus("WARM_UP")], "carelink-connector", WriteOrigin.Live);

        LiveSensorStateReports().Should().BeEmpty("a resend must not revive the deleted status's report");
    }

    private const string CareLinkStatusId = "6500000000000000000000d5";

    private static DeviceStatus CareLinkStatus(string sensorState) => new()
    {
        Id = CareLinkStatusId, Mills = At, Device = "CareLink MiniMed 780G",
        Cgm = new CgmStatus { Manufacturer = "Medtronic", SensorState = sensorState },
        Uploader = new UploaderStatus { Battery = 80 },
    };

    private List<V4Models.SensorStateReport> LiveSensorStateReports() =>
        [.. _context.SensorStateReports.IgnoreQueryFilters().Where(e => e.DeletedAt == null).AsEnumerable()
            .Select(Nocturne.Infrastructure.Data.Mappers.V4.SensorStateReportMapper.ToDomainModel)];

    [Fact]
    public async Task Activity_BatchResend_UpdatesTheHeartRateRow()
    {
        var decomposer = new ActivityDecomposer(
            new ActivityRecordRepository(_context.AsFactory(), TimeProvider.System), Mock.Of<IStateSpanRepository>(), Mock.Of<ISleepService>(), TimeProvider.System, NullLogger<ActivityDecomposer>.Instance);

        Activity HeartRate(int bpm) => new()
        {
            Id = "6500000000000000000000a1", Mills = At, EnteredBy = "watch",
            AdditionalProperties = new Dictionary<string, object> { ["bpm"] = bpm },
        };

        await decomposer.DecomposeBatchAsync([HeartRate(60)], WriteOrigin.Live);

        var result = await decomposer.DecomposeBatchAsync([HeartRate(90)], WriteOrigin.Live);

        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().ContainSingle();
        _context.HeartRates.Select(e => e.Bpm).Should().Equal(90);
    }
}
