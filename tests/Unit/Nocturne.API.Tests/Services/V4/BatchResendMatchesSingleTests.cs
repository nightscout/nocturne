using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

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
    private readonly TestTenantDbContextFactory _factory;
    private readonly IAuditContext _audit = Mock.Of<IAuditContext>();
    private readonly IDeduplicationService _dedup = Mock.Of<IDeduplicationService>();

    public BatchResendMatchesSingleTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();
        _factory = new TestTenantDbContextFactory(_context);
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
        var decomposer = new EntryDecomposer(
            _context,
            new SensorGlucoseRepository(_factory, _dedup, _audit, NullLogger<SensorGlucoseRepository>.Instance),
            new MeterGlucoseRepository(_factory, _audit, NullLogger<MeterGlucoseRepository>.Instance),
            new CalibrationRepository(_factory, _audit, NullLogger<CalibrationRepository>.Instance),
            Mock.Of<IGlucoseProcessingResolver>(),
            Mock.Of<IPatientDeviceStamper>(),
            _audit,
            NullLogger<EntryDecomposer>.Instance);

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
        var decomposer = new TreatmentDecomposer(
            _context,
            new BolusRepository(_factory, _dedup, _audit, NullLogger<BolusRepository>.Instance),
            new TempBasalRepository(_factory, _dedup, _audit, NullLogger<TempBasalRepository>.Instance),
            new CarbIntakeRepository(_factory, _dedup, _audit, NullLogger<CarbIntakeRepository>.Instance),
            new BGCheckRepository(_factory, _dedup, _audit, NullLogger<BGCheckRepository>.Instance),
            new NoteRepository(_factory, _dedup, _audit, NullLogger<NoteRepository>.Instance),
            new DeviceEventRepository(_factory, _dedup, _audit, NullLogger<DeviceEventRepository>.Instance),
            new BolusCalculationRepository(_factory, _dedup, _audit, NullLogger<BolusCalculationRepository>.Instance),
            Mock.Of<IStateSpanService>(),
            foods.Object,
            Mock.Of<IDeviceService>(),
            Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<IProfileDecomposer>(),
            Mock.Of<IActiveProfileResolver>(),
            Mock.Of<IPatientInsulinRepository>(),
            _audit,
            _dedup,
            NullLogger<TreatmentDecomposer>.Instance);

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

        await decomposer.DecomposeAsync(Carbs(20), WriteOrigin.Live);
        await decomposer.DecomposeAsync(Note("before"), WriteOrigin.Live);
        await decomposer.DecomposeAsync(TempBasal(0.5), WriteOrigin.Live);

        var result = await decomposer.DecomposeBatchAsync(
            [Carbs(35), Note("after"), TempBasal(1.5)], WriteOrigin.Live);

        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().HaveCount(3);
        _context.CarbIntakes.Select(e => e.Carbs).Should().Equal(35);
        _context.Notes.Select(e => e.Text).Should().Equal("after");
        _context.TempBasals.Select(e => e.Rate).Should().Equal(1.5);
        foods.Verify(
            s => s.AddAsync(It.IsAny<TreatmentFood>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the food line is seeded on create only, so a resend leaves a user's deletion of it standing");
    }

    [Fact]
    public async Task DeviceStatus_BatchResend_UpdatesTheSnapshotRow()
    {
        var decomposer = new DeviceStatusDecomposer(
            new ApsSnapshotRepository(_factory, _audit, NullLogger<ApsSnapshotRepository>.Instance),
            new PumpSnapshotRepository(_factory, _audit, NullLogger<PumpSnapshotRepository>.Instance),
            new UploaderSnapshotRepository(_factory, _audit, NullLogger<UploaderSnapshotRepository>.Instance),
            new DeviceStatusExtrasRepository(_factory, _audit, NullLogger<DeviceStatusExtrasRepository>.Instance),
            Mock.Of<IStateSpanService>(),
            Mock.Of<IDeviceService>(),
            _audit,
            NullLogger<DeviceStatusDecomposer>.Instance);

        DeviceStatus Status(int battery) => new()
        {
            Id = "6500000000000000000000d1", Mills = At, Device = "phone", UploaderBattery = battery,
        };

        await decomposer.DecomposeAsync(Status(80), source: null, WriteOrigin.Live);

        var result = await decomposer.DecomposeBatchAsync([Status(40)], source: null, WriteOrigin.Live);

        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().ContainSingle();
        _context.UploaderSnapshots.Select(e => e.Battery).Should().Equal(40);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeviceStatus_ResendWithExtras_KeepsOneExtrasRowThatADeleteRemoves(bool batch)
    {
        var decomposer = new DeviceStatusDecomposer(
            new ApsSnapshotRepository(_factory, _audit, NullLogger<ApsSnapshotRepository>.Instance),
            new PumpSnapshotRepository(_factory, _audit, NullLogger<PumpSnapshotRepository>.Instance),
            new UploaderSnapshotRepository(_factory, _audit, NullLogger<UploaderSnapshotRepository>.Instance),
            new DeviceStatusExtrasRepository(_factory, _audit, NullLogger<DeviceStatusExtrasRepository>.Instance),
            Mock.Of<IStateSpanService>(),
            Mock.Of<IDeviceService>(),
            _audit,
            NullLogger<DeviceStatusDecomposer>.Instance);

        const string legacyId = "6500000000000000000000d2";
        DeviceStatus Status(int battery) => new()
        {
            Id = legacyId, Mills = At, Device = "phone", UploaderBattery = battery,
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["configuration"] = JsonDocument.Parse("""{"version":1}""").RootElement,
            },
        };

        Task Send(DeviceStatus ds) => batch
            ? decomposer.DecomposeBatchAsync([ds], source: null, WriteOrigin.Live)
            : decomposer.DecomposeAsync(ds, source: null, WriteOrigin.Live);

        await Send(Status(80));
        var storedCorrelationId = _context.UploaderSnapshots.Single().CorrelationId;

        await Send(Status(40));

        _context.UploaderSnapshots.Select(e => e.CorrelationId).Should().Equal(storedCorrelationId);
        _context.DeviceStatusExtras.IgnoreQueryFilters().Where(e => e.DeletedAt == null)
            .Select(e => (Guid?)e.CorrelationId).Should().Equal(storedCorrelationId);

        await decomposer.DeleteByLegacyIdAsync(legacyId, WriteOrigin.Live);

        _context.DeviceStatusExtras.IgnoreQueryFilters().Where(e => e.DeletedAt == null)
            .Should().BeEmpty("deleting the device status must reach its extras row");
    }

    [Fact]
    public async Task DeviceStatus_SingleResendAfterUserDelete_WritesNoExtrasRow()
    {
        var decomposer = new DeviceStatusDecomposer(
            new ApsSnapshotRepository(_factory, _audit, NullLogger<ApsSnapshotRepository>.Instance),
            new PumpSnapshotRepository(_factory, _audit, NullLogger<PumpSnapshotRepository>.Instance),
            new UploaderSnapshotRepository(_factory, _audit, NullLogger<UploaderSnapshotRepository>.Instance),
            new DeviceStatusExtrasRepository(_factory, _audit, NullLogger<DeviceStatusExtrasRepository>.Instance),
            Mock.Of<IStateSpanService>(),
            Mock.Of<IDeviceService>(),
            _audit,
            NullLogger<DeviceStatusDecomposer>.Instance);

        const string legacyId = "6500000000000000000000d3";
        DeviceStatus Status() => new()
        {
            Id = legacyId, Mills = At, Device = "phone", UploaderBattery = 80,
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["configuration"] = JsonDocument.Parse("""{"version":1}""").RootElement,
            },
        };

        await decomposer.DecomposeAsync(Status(), source: null, WriteOrigin.Live);
        await decomposer.DeleteByLegacyIdAsync(legacyId, WriteOrigin.Live);

        var result = await decomposer.DecomposeAsync(Status(), source: null, WriteOrigin.Live);

        result.CreatedRecords.Should().BeEmpty();
        _context.DeviceStatusExtras.IgnoreQueryFilters().Where(e => e.DeletedAt == null)
            .Should().BeEmpty("a resend must not revive the deleted group's extras under a fresh correlation id");
    }

    [Fact]
    public async Task Activity_BatchResend_UpdatesTheHeartRateRow()
    {
        var decomposer = new ActivityDecomposer(
            _context, Mock.Of<IStateSpanRepository>(), NullLogger<ActivityDecomposer>.Instance);

        Activity HeartRate(int bpm) => new()
        {
            Id = "6500000000000000000000a1", Mills = At, EnteredBy = "watch",
            AdditionalProperties = new Dictionary<string, object> { ["bpm"] = bpm },
        };

        await decomposer.DecomposeAsync(HeartRate(60), WriteOrigin.Live);

        var result = await decomposer.DecomposeBatchAsync([HeartRate(90)], WriteOrigin.Live);

        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().ContainSingle();
        _context.HeartRates.Select(e => e.Bpm).Should().Equal(90);
    }
}
