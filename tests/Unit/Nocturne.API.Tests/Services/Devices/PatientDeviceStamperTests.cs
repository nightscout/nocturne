using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Timezones;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Timezones;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Devices;

[Trait("Category", "Unit")]
public class PatientDeviceStamperTests
{
    private readonly Mock<IPatientDeviceRepository> _mockRepository;
    private readonly Mock<ITimezoneTimelineService> _mockTimezoneTimeline;
    private readonly PatientDeviceStamper _stamper;

    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    public PatientDeviceStamperTests()
    {
        _mockRepository = new Mock<IPatientDeviceRepository>();
        _mockTimezoneTimeline = new Mock<ITimezoneTimelineService>();
        // Empty timeline: records without a UtcOffset resolve to their UTC date.
        _mockTimezoneTimeline
            .Setup(s => s.GetResolverAsync(It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TimezoneTimeline([]));
        _stamper = new PatientDeviceStamper(
            _mockRepository.Object, _mockTimezoneTimeline.Object, NullLogger<PatientDeviceStamper>.Instance);
    }

    private void SetupDevices(params PatientDevice[] devices)
    {
        _mockRepository
            .Setup(r => r.GetByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(devices);
    }

    private static PatientDevice Cgm(
        string manufacturer,
        Guid? id = null,
        string? serial = null,
        string? catalogId = null,
        DateOnly? start = null,
        DateOnly? end = null,
        int? rank = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        DeviceCategory = DeviceCategory.CGM,
        Manufacturer = manufacturer,
        Model = manufacturer + " CGM",
        SerialNumber = serial,
        CatalogId = catalogId,
        StartDate = start,
        EndDate = end,
        Rank = rank,
        IsCurrent = end is null,
    };

    private static SensorGlucose Reading(string? dataSource = null, string? device = null, DateTime? timestamp = null, int? utcOffset = null) => new()
    {
        Mgdl = 120,
        Timestamp = timestamp ?? Now,
        DataSource = dataSource,
        Device = device,
        UtcOffset = utcOffset,
    };

    [Fact]
    public async Task SingleActiveDevice_Stamps_EvenWithoutManufacturerMapping()
    {
        var device = Cgm("Custom");
        SetupDevices(device);
        var record = Reading(dataSource: DataSources.XDrip);

        await _stamper.StampAsync([record], [DeviceCategory.CGM], DataSources.XDrip);

        record.PatientDeviceId.Should().Be(device.Id);
    }

    [Fact]
    public async Task NoRegisteredDevices_LeavesNull()
    {
        SetupDevices();
        var record = Reading(dataSource: DataSources.DexcomConnector);

        await _stamper.StampAsync([record], [DeviceCategory.CGM], DataSources.DexcomConnector);

        record.PatientDeviceId.Should().BeNull();
    }

    [Fact]
    public async Task WrongCategoryDevices_LeaveNull()
    {
        var pump = Cgm("Insulet");
        pump.DeviceCategory = DeviceCategory.InsulinPump;
        SetupDevices(pump);
        var record = Reading(dataSource: DataSources.DexcomConnector);

        await _stamper.StampAsync([record], [DeviceCategory.CGM], DataSources.DexcomConnector);

        record.PatientDeviceId.Should().BeNull();
    }

    [Fact]
    public async Task AlreadyStampedRecord_IsNotOverwritten()
    {
        var existing = Guid.NewGuid();
        SetupDevices(Cgm("Dexcom"));
        var record = Reading(dataSource: DataSources.DexcomConnector);
        record.PatientDeviceId = existing;

        await _stamper.StampAsync([record], [DeviceCategory.CGM], DataSources.DexcomConnector);

        record.PatientDeviceId.Should().Be(existing);
    }

    [Fact]
    public async Task TwoManufacturers_SourceDisambiguates()
    {
        var dexcom = Cgm("Dexcom");
        var libre = Cgm("Abbott");
        SetupDevices(dexcom, libre);

        var dexcomRecord = Reading(dataSource: DataSources.DexcomConnector);
        var libreRecord = Reading(dataSource: DataSources.LibreConnector);

        await _stamper.StampAsync([dexcomRecord, libreRecord], [DeviceCategory.CGM], batchSource: null);

        dexcomRecord.PatientDeviceId.Should().Be(dexcom.Id);
        libreRecord.PatientDeviceId.Should().Be(libre.Id);
    }

    [Fact]
    public async Task TwoSameManufacturerDevices_SameSource_LeavesNull()
    {
        SetupDevices(Cgm("Dexcom"), Cgm("Dexcom"));
        var record = Reading(dataSource: DataSources.DexcomConnector);

        await _stamper.StampAsync([record], [DeviceCategory.CGM], DataSources.DexcomConnector);

        record.PatientDeviceId.Should().BeNull();
    }

    [Fact]
    public async Task TwoSameManufacturerDevices_SerialDisambiguates()
    {
        var older = Cgm("Dexcom", serial: "SM12345678");
        var newer = Cgm("Dexcom", serial: "SM87654321");
        SetupDevices(older, newer);
        var record = Reading(dataSource: DataSources.DexcomConnector, device: "Dexcom G7 SM87654321");

        await _stamper.StampAsync([record], [DeviceCategory.CGM], DataSources.DexcomConnector);

        record.PatientDeviceId.Should().Be(newer.Id);
    }

    [Fact]
    public async Task OverlappingAggregatorSource_TwoDevices_LeavesNull()
    {
        SetupDevices(Cgm("Dexcom"), Cgm("Abbott"));
        var record = Reading(dataSource: DataSources.GlookoConnector);

        await _stamper.StampAsync([record], [DeviceCategory.CGM], DataSources.GlookoConnector);

        record.PatientDeviceId.Should().BeNull();
    }

    [Fact]
    public async Task HistoricalRecord_StampsDeviceWornAtThatTime()
    {
        var retired = Cgm("Dexcom",
            start: new DateOnly(2026, 1, 1),
            end: new DateOnly(2026, 3, 31));
        var current = Cgm("Abbott",
            start: new DateOnly(2026, 4, 1));
        SetupDevices(retired, current);

        var backfilled = Reading(
            dataSource: DataSources.NightscoutConnector,
            timestamp: new DateTime(2026, 2, 10, 8, 0, 0, DateTimeKind.Utc));

        await _stamper.StampAsync([backfilled], [DeviceCategory.CGM], DataSources.NightscoutConnector);

        backfilled.PatientDeviceId.Should().Be(retired.Id);
    }

    [Fact]
    public async Task ManufacturerSpecificSource_ContradictingSoleDevice_LeavesNull()
    {
        SetupDevices(Cgm("Dexcom"));
        var record = Reading(dataSource: DataSources.LibreConnector);

        await _stamper.StampAsync([record], [DeviceCategory.CGM], DataSources.LibreConnector);

        record.PatientDeviceId.Should().BeNull();
    }

    [Fact]
    public async Task CatalogManufacturer_TakesPrecedenceOverManufacturerField()
    {
        var device = Cgm("SomeOldValue", catalogId: "dexcom-g7");
        var other = Cgm("Abbott");
        SetupDevices(device, other);
        var record = Reading(dataSource: DataSources.DexcomConnector);

        await _stamper.StampAsync([record], [DeviceCategory.CGM], DataSources.DexcomConnector);

        record.PatientDeviceId.Should().Be(device.Id);
    }

    [Fact]
    public async Task BatchSource_UsedWhenRecordHasNoDataSource()
    {
        var dexcom = Cgm("Dexcom");
        var libre = Cgm("Abbott");
        SetupDevices(dexcom, libre);
        var record = Reading(dataSource: null);

        await _stamper.StampAsync([record], [DeviceCategory.CGM], DataSources.DexcomConnector);

        record.PatientDeviceId.Should().Be(dexcom.Id);
    }

    [Fact]
    public async Task RepositoryFailure_IsSwallowed_RecordsProceedUnstamped()
    {
        _mockRepository
            .Setup(r => r.GetByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB error"));
        var record = Reading(dataSource: DataSources.DexcomConnector);

        var act = () => _stamper.StampAsync([record], [DeviceCategory.CGM], DataSources.DexcomConnector);

        await act.Should().NotThrowAsync();
        record.PatientDeviceId.Should().BeNull();
    }

    [Fact]
    public async Task AllRecordsAlreadyStamped_SkipsRepositoryLookup()
    {
        var record = Reading(dataSource: DataSources.DexcomConnector);
        record.PatientDeviceId = Guid.NewGuid();

        await _stamper.StampAsync([record], [DeviceCategory.CGM], DataSources.DexcomConnector);

        _mockRepository.Verify(
            r => r.GetByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task MedtronicDevice_MatchesBothCareLinkAndMiniMedSources()
    {
        var medtronic = Cgm("Medtronic");
        var dexcom = Cgm("Dexcom");
        SetupDevices(medtronic, dexcom);

        var carelinkRecord = Reading(dataSource: DataSources.CareLinkConnector);
        var minimedRecord = Reading(dataSource: DataSources.MiniMedConnector);

        await _stamper.StampAsync([carelinkRecord, minimedRecord], [DeviceCategory.CGM], batchSource: null);

        carelinkRecord.PatientDeviceId.Should().Be(medtronic.Id);
        minimedRecord.PatientDeviceId.Should().Be(medtronic.Id);
    }

    [Fact]
    public async Task ShortSerial_DoesNotSubstringMatch()
    {
        // "G6" occurs incidentally in device strings; a 2-char serial must not beat the
        // source-compatibility check.
        var wrong = Cgm("Abbott", serial: "G6");
        var right = Cgm("Dexcom");
        SetupDevices(wrong, right);
        var record = Reading(dataSource: DataSources.DexcomConnector, device: "xDrip-DexcomG6");

        await _stamper.StampAsync([record], [DeviceCategory.CGM], DataSources.DexcomConnector);

        record.PatientDeviceId.Should().Be(right.Id);
    }

    [Fact]
    public async Task UtcOffset_MatchesOnWearerLocalDate_AcrossUtcDayBoundary()
    {
        // Device starts 2026-06-15 (wearer-local). 21:00Z on the 14th is already 07:00 on the
        // 15th at UTC+10, so the offset-carrying record attributes and the UTC one doesn't.
        var device = Cgm("Dexcom", start: new DateOnly(2026, 6, 15));
        SetupDevices(device);
        var beforeMidnightUtc = new DateTime(2026, 6, 14, 21, 0, 0, DateTimeKind.Utc);

        var localRecord = Reading(dataSource: DataSources.DexcomConnector, timestamp: beforeMidnightUtc, utcOffset: 600);
        var utcRecord = Reading(dataSource: DataSources.DexcomConnector, timestamp: beforeMidnightUtc, utcOffset: 0);

        await _stamper.StampAsync([localRecord, utcRecord], [DeviceCategory.CGM], DataSources.DexcomConnector);

        localRecord.PatientDeviceId.Should().Be(device.Id);
        utcRecord.PatientDeviceId.Should().BeNull();
    }

    [Fact]
    public async Task MissingUtcOffset_FallsBackToTenantTimezoneTimeline()
    {
        _mockTimezoneTimeline
            .Setup(s => s.GetResolverAsync(It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TimezoneTimeline([
                new TimezoneTimelineEntry { Timezone = "Australia/Brisbane", EffectiveFrom = new DateTime(2020, 1, 1) },
            ]));

        var device = Cgm("Dexcom", start: new DateOnly(2026, 6, 15));
        SetupDevices(device);
        var record = Reading(
            dataSource: DataSources.DexcomConnector,
            timestamp: new DateTime(2026, 6, 14, 21, 0, 0, DateTimeKind.Utc));

        await _stamper.StampAsync([record], [DeviceCategory.CGM], DataSources.DexcomConnector);

        record.PatientDeviceId.Should().Be(device.Id);
    }

    [Fact]
    public async Task RecordOutsideEveryDeviceWindow_LeavesNull()
    {
        var device = Cgm("Dexcom", start: new DateOnly(2026, 6, 1));
        SetupDevices(device);
        var record = Reading(
            dataSource: DataSources.DexcomConnector,
            timestamp: new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));

        await _stamper.StampAsync([record], [DeviceCategory.CGM], DataSources.DexcomConnector);

        record.PatientDeviceId.Should().BeNull();
    }
}
