using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Devices;

[Trait("Category", "Unit")]
public class DeviceReattributionServiceTests
{
    private const int ExpectedCap = 50_000;

    private readonly Mock<ISensorGlucoseRepository> _sensorGlucose = new();
    private readonly Mock<IMeterGlucoseRepository> _meterGlucose = new();
    private readonly Mock<IBolusRepository> _boluses = new();
    private readonly Mock<ITempBasalRepository> _tempBasals = new();
    private readonly Mock<IBasalInjectionRepository> _basalInjections = new();
    private readonly Mock<IDeviceEventRepository> _deviceEvents = new();
    private readonly Mock<IPatientDeviceStamper> _stamper = new();
    private readonly DeviceReattributionService _service;

    /// <summary>Categories the stamper was asked to match, in call order.</summary>
    private readonly List<IReadOnlyList<DeviceCategory>> _stampedCategories = [];

    /// <summary>Device the stubbed matching ladder attributes every record it is handed to.</summary>
    private Guid? _ladderMatches;

    public DeviceReattributionServiceTests()
    {
        _service = new DeviceReattributionService(
            _sensorGlucose.Object, _meterGlucose.Object, _boluses.Object, _tempBasals.Object,
            _basalInjections.Object, _deviceEvents.Object, _stamper.Object,
            NullLogger<DeviceReattributionService>.Instance);

        _stamper.Setup(s => s.StampAsync(
                It.IsAny<IReadOnlyList<IDeviceAttributed>>(), It.IsAny<IReadOnlyList<DeviceCategory>>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<IDeviceAttributed>, IReadOnlyList<DeviceCategory>, string?, CancellationToken>(
                (records, categories, _, _) =>
                {
                    _stampedCategories.Add(categories);
                    if (_ladderMatches is { } deviceId)
                        foreach (var record in records)
                            record.PatientDeviceId = deviceId;
                })
            .Returns(Task.CompletedTask);
    }

    private static PatientDevice Device(DeviceCategory category, DateOnly? start = null, DateOnly? end = null) => new()
    {
        Id = Guid.NewGuid(),
        DeviceCategory = category,
        Manufacturer = "Dexcom",
        Model = "G7",
        StartDate = start,
        EndDate = end,
    };

    /// <summary>
    /// Every stream holds one unattributed record, and the matching ladder attributes whatever it is
    /// handed to <paramref name="deviceId"/> — so each stream the service reads contributes exactly 1.
    /// </summary>
    private void SetupAllStreams(Guid deviceId)
    {
        _ladderMatches = deviceId;

        Setup(_sensorGlucose, new SensorGlucose { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Mgdl = 120 });
        Setup(_meterGlucose, new MeterGlucose { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Mgdl = 120 });
        Setup(_boluses, new Bolus { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Insulin = 1 });
        Setup(_tempBasals, new TempBasal { Id = Guid.NewGuid(), StartTimestamp = DateTime.UtcNow, Rate = 1 });
        Setup(_basalInjections, new BasalInjection { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Units = 20 });

        var deviceEvent = new DeviceEvent { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, EventType = DeviceEventType.SiteChange };
        _deviceEvents.Setup(r => r.GetUnattributedAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<IReadOnlyCollection<DeviceEventType>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([deviceEvent]);
        _deviceEvents.Setup(r => r.SetPatientDeviceIdsAsync(
                It.IsAny<IReadOnlyDictionary<Guid, Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
    }

    private static void Setup<TRepo, TRecord>(Mock<TRepo> repo, TRecord record)
        where TRepo : class, IDeviceAttributedRepository<TRecord>
        where TRecord : class, IDeviceAttributed
    {
        repo.Setup(r => r.GetUnattributedAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([record]);
        repo.Setup(r => r.SetPatientDeviceIdsAsync(
                It.IsAny<IReadOnlyDictionary<Guid, Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
    }

    private void VerifyNotRead<TRepo, TRecord>(Mock<TRepo> repo)
        where TRepo : class, IDeviceAttributedRepository<TRecord>
        where TRecord : class, IDeviceAttributed
    {
        repo.Verify(r => r.GetUnattributedAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(r => r.SetPatientDeviceIdsAsync(
            It.IsAny<IReadOnlyDictionary<Guid, Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void VerifyDeviceEventsNotRead() => _deviceEvents.Verify(r => r.GetUnattributedAsync(
        It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<IReadOnlyCollection<DeviceEventType>>(),
        It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task Cgm_BackStampsSensorGlucoseAndSensorEvents_LeavingPumpStreamsUntouched()
    {
        var device = Device(DeviceCategory.CGM);
        SetupAllStreams(device.Id);

        var result = await _service.ReattributeForDeviceAsync(device);

        result.Should().Be(2, "sensor glucose and sensor device events are the CGM's streams");
        VerifyNotRead<IBolusRepository, Bolus>(_boluses);
        VerifyNotRead<ITempBasalRepository, TempBasal>(_tempBasals);
        VerifyNotRead<IBasalInjectionRepository, BasalInjection>(_basalInjections);
        VerifyNotRead<IMeterGlucoseRepository, MeterGlucose>(_meterGlucose);
        _deviceEvents.Verify(r => r.GetUnattributedAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.Is<IReadOnlyCollection<DeviceEventType>>(t => t.SequenceEqual(DeviceAttributionCategories.SensorEventTypes)),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _stampedCategories.Should().AllSatisfy(c => c.Should().Equal(DeviceCategory.CGM));
    }

    [Fact]
    public async Task InsulinPump_BackStampsBolusesTempBasalsAndPumpEvents_LeavingGlucoseStreamsUntouched()
    {
        var device = Device(DeviceCategory.InsulinPump);
        SetupAllStreams(device.Id);

        var result = await _service.ReattributeForDeviceAsync(device);

        result.Should().Be(3, "boluses, temp basals and pump device events are the pump's streams");
        VerifyNotRead<ISensorGlucoseRepository, SensorGlucose>(_sensorGlucose);
        VerifyNotRead<IMeterGlucoseRepository, MeterGlucose>(_meterGlucose);
        VerifyNotRead<IBasalInjectionRepository, BasalInjection>(_basalInjections);
        _deviceEvents.Verify(r => r.GetUnattributedAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.Is<IReadOnlyCollection<DeviceEventType>>(t =>
                t.SequenceEqual(DeviceAttributionCategories.PumpEventTypes)
                && !t.Any(DeviceAttributionCategories.IsSensorEvent)),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SmartPen_BackStampsBolusesAndBasalInjections()
    {
        var device = Device(DeviceCategory.SmartPen);
        SetupAllStreams(device.Id);

        var result = await _service.ReattributeForDeviceAsync(device);

        result.Should().Be(2);
        VerifyNotRead<ITempBasalRepository, TempBasal>(_tempBasals);
        VerifyNotRead<ISensorGlucoseRepository, SensorGlucose>(_sensorGlucose);
        VerifyDeviceEventsNotRead();
        // The pen's boluses are matched against every category ingest lets own a bolus, not just pens.
        _stampedCategories.Should().Contain(c => c.SequenceEqual(DeviceAttributionCategories.Bolus));
        _stampedCategories.Should().Contain(c => c.SequenceEqual(DeviceAttributionCategories.BasalInjection));
    }

    [Fact]
    public async Task InsulinPen_BackStampsBasalInjectionsOnly()
    {
        var device = Device(DeviceCategory.InsulinPen);
        SetupAllStreams(device.Id);

        var result = await _service.ReattributeForDeviceAsync(device);

        result.Should().Be(1);
        VerifyNotRead<IBolusRepository, Bolus>(_boluses);
        VerifyDeviceEventsNotRead();
    }

    [Fact]
    public async Task GlucoseMeter_BackStampsMeterGlucoseOnly()
    {
        var device = Device(DeviceCategory.GlucoseMeter);
        SetupAllStreams(device.Id);

        var result = await _service.ReattributeForDeviceAsync(device);

        result.Should().Be(1);
        VerifyNotRead<ISensorGlucoseRepository, SensorGlucose>(_sensorGlucose);
        VerifyDeviceEventsNotRead();
        _stampedCategories.Should().ContainSingle()
            .Which.Should().Equal(DeviceCategory.GlucoseMeter);
    }

    [Fact]
    public async Task Uploader_OwnsNoRecordType_AndTouchesNothing()
    {
        var device = Device(DeviceCategory.Uploader);
        SetupAllStreams(device.Id);

        var result = await _service.ReattributeForDeviceAsync(device);

        result.Should().Be(0);
        VerifyNotRead<ISensorGlucoseRepository, SensorGlucose>(_sensorGlucose);
        VerifyNotRead<IMeterGlucoseRepository, MeterGlucose>(_meterGlucose);
        VerifyNotRead<IBolusRepository, Bolus>(_boluses);
        VerifyNotRead<ITempBasalRepository, TempBasal>(_tempBasals);
        VerifyNotRead<IBasalInjectionRepository, BasalInjection>(_basalInjections);
        VerifyDeviceEventsNotRead();
        _stamper.Verify(s => s.StampAsync(
            It.IsAny<IReadOnlyList<IDeviceAttributed>>(), It.IsAny<IReadOnlyList<DeviceCategory>>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EveryStream_IsWindowedToTheUsageDates_AndCapped()
    {
        var device = Device(DeviceCategory.InsulinPump, start: new DateOnly(2026, 6, 1), end: new DateOnly(2026, 6, 30));
        SetupAllStreams(device.Id);

        // Usage window is padded ±1 day around the device's local dates.
        var expectedFrom = new DateOnly(2026, 6, 1).ToDateTime(TimeOnly.MinValue).AddDays(-1);
        var expectedTo = new DateOnly(2026, 6, 30).ToDateTime(TimeOnly.MaxValue).AddDays(1);

        await _service.ReattributeForDeviceAsync(device);

        _boluses.Verify(r => r.GetUnattributedAsync(expectedFrom, expectedTo, ExpectedCap, It.IsAny<CancellationToken>()), Times.Once);
        _tempBasals.Verify(r => r.GetUnattributedAsync(expectedFrom, expectedTo, ExpectedCap, It.IsAny<CancellationToken>()), Times.Once);
        _deviceEvents.Verify(r => r.GetUnattributedAsync(
            expectedFrom, expectedTo, It.IsAny<IReadOnlyCollection<DeviceEventType>>(), ExpectedCap,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpenEndedUsageWindow_PassesNullBounds()
    {
        var device = Device(DeviceCategory.CGM);
        SetupAllStreams(device.Id);

        await _service.ReattributeForDeviceAsync(device);

        _sensorGlucose.Verify(r => r.GetUnattributedAsync(null, null, ExpectedCap, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PersistsOnlyTheRecordsTheLadderAttributed()
    {
        var device = Device(DeviceCategory.CGM, start: new DateOnly(2026, 6, 1));
        var matched = new SensorGlucose { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Mgdl = 120, DataSource = "dexcom" };
        var unmatched = new SensorGlucose { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Mgdl = 118, DataSource = "libre" };

        // The ladder attributes only the reading whose source matches the registered device.
        _stamper.Setup(s => s.StampAsync(
                It.IsAny<IReadOnlyList<IDeviceAttributed>>(), It.IsAny<IReadOnlyList<DeviceCategory>>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => matched.PatientDeviceId = device.Id)
            .Returns(Task.CompletedTask);
        _sensorGlucose.Setup(r => r.GetUnattributedAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([matched, unmatched]);
        _deviceEvents.Setup(r => r.GetUnattributedAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<IReadOnlyCollection<DeviceEventType>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        IReadOnlyDictionary<Guid, Guid>? persisted = null;
        _sensorGlucose.Setup(r => r.SetPatientDeviceIdsAsync(
                It.IsAny<IReadOnlyDictionary<Guid, Guid>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyDictionary<Guid, Guid>, CancellationToken>((map, _) => persisted = map)
            .ReturnsAsync(1);

        var result = await _service.ReattributeForDeviceAsync(device);

        result.Should().Be(1);
        persisted.Should().NotBeNull();
        persisted.Should().ContainKey(matched.Id).WhoseValue.Should().Be(device.Id);
        persisted.Should().NotContainKey(unmatched.Id);
    }

    [Fact]
    public async Task NoUnattributedRecords_SkipsStampingAndPersisting()
    {
        var device = Device(DeviceCategory.CGM, start: new DateOnly(2026, 6, 1));
        _sensorGlucose.Setup(r => r.GetUnattributedAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _deviceEvents.Setup(r => r.GetUnattributedAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<IReadOnlyCollection<DeviceEventType>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await _service.ReattributeForDeviceAsync(device);

        result.Should().Be(0);
        _stamper.Verify(s => s.StampAsync(
            It.IsAny<IReadOnlyList<IDeviceAttributed>>(), It.IsAny<IReadOnlyList<DeviceCategory>>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _sensorGlucose.Verify(r => r.SetPatientDeviceIdsAsync(
            It.IsAny<IReadOnlyDictionary<Guid, Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnattributedButUnmatched_DoesNotPersist()
    {
        var device = Device(DeviceCategory.InsulinPump, start: new DateOnly(2026, 6, 1));
        // The ladder leaves the record unattributed (no matching device), so nothing is written.
        _boluses.Setup(r => r.GetUnattributedAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Bolus { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Insulin = 1 }]);
        _tempBasals.Setup(r => r.GetUnattributedAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _deviceEvents.Setup(r => r.GetUnattributedAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<IReadOnlyCollection<DeviceEventType>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await _service.ReattributeForDeviceAsync(device);

        result.Should().Be(0);
        _boluses.Verify(r => r.SetPatientDeviceIdsAsync(
            It.IsAny<IReadOnlyDictionary<Guid, Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
