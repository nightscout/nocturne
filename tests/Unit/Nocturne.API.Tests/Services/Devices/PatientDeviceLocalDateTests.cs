using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Devices;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Timezones;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.Devices;

/// <summary>
/// A device's dates are local calendar dates, so the two ingest paths that match a record to a
/// device, <see cref="DeviceService.ResolvePatientDeviceAsync"/> and
/// <see cref="PatientDeviceStamper"/>, and the device windows of <see cref="AidSystemMetricsReader"/>
/// must place a record in the hours where the wearer's date is already the next one on the same
/// device.
/// </summary>
[Trait("Category", "Unit")]
public class PatientDeviceLocalDateTests
{
    private const string Adelaide = "Australia/Adelaide";

    private readonly Guid _deviceId = Guid.CreateVersion7();
    private readonly PatientDevice _outgoing;
    private readonly PatientDevice _incoming;
    private readonly Mock<IPatientDeviceRepository> _patientDevices = new();

    public PatientDeviceLocalDateTests()
    {
        _outgoing = Cgm(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 10));
        _incoming = Cgm(new DateOnly(2026, 6, 11), null);
        _patientDevices
            .Setup(r => r.GetByDeviceIdAsync(_deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([_outgoing, _incoming]);
        _patientDevices
            .Setup(r => r.GetByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([_outgoing, _incoming]);
    }

    // 2026-06-10 15:00Z is 00:30 on 11 June in Adelaide (+9:30): the new device's first day.
    private static readonly DateTime FirstHoursOfTheNewDay = new(2026, 6, 10, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LastHourOfTheOldDay = new(2026, 6, 10, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task BothIngestPaths_MatchOnTheLocalDateOfTheTimeline()
    {
        var deviceDates = DeviceDateClockDoubles.Timeline(Adelaide);

        (await ResolveAsync(deviceDates, FirstHoursOfTheNewDay)).Should().Be(_incoming.Id);
        (await StampAsync(deviceDates, FirstHoursOfTheNewDay)).Should().Be(_incoming.Id);
    }

    [Fact]
    public async Task BothIngestPaths_MatchOnTheLocalDateOfTheProfileZone_WhenTheTimelineIsEmpty()
    {
        var deviceDates = DeviceDateClockDoubles.Resolver(profileZone: Adelaide);

        (await ResolveAsync(deviceDates, FirstHoursOfTheNewDay)).Should().Be(_incoming.Id);
        (await StampAsync(deviceDates, FirstHoursOfTheNewDay)).Should().Be(_incoming.Id);
    }

    [Fact]
    public async Task ATimelineEntry_PlacesTheDatesItCovers_AndTheProfileZoneTheRest()
    {
        var deviceDates = DeviceDateClockDoubles.Resolver(
            profileZone: "Europe/London",
            timeline: [new TimezoneTimelineEntry { EffectiveFrom = new DateTime(2026, 6, 8), Timezone = Adelaide }]);

        (await ResolveAsync(deviceDates, FirstHoursOfTheNewDay)).Should().Be(_incoming.Id);
        (await StampAsync(deviceDates, FirstHoursOfTheNewDay)).Should().Be(_incoming.Id);

        var windows = await MetricsWindowsAsync(deviceDates);
        windows[_incoming.Id].Start.Should().Be(new DateTime(2026, 6, 10, 14, 30, 0, DateTimeKind.Utc), "the trip puts 11 June in Adelaide");
        windows[_outgoing.Id].Start.Should().Be(new DateTime(2026, 5, 31, 23, 0, 0, DateTimeKind.Utc), "1 June predates the trip, so London");
    }

    [Theory]
    [InlineData(14)]
    [InlineData(15)]
    public async Task IngestAndTheAidMetrics_PutAReadingOnTheSameDevice_OnADeviceChangeDay(int hour)
    {
        var deviceDates = DeviceDateClockDoubles.Resolver(profileZone: Adelaide);
        var reading = new DateTime(2026, 6, 10, hour, 0, 0, DateTimeKind.Utc);

        var matched = await StampAsync(deviceDates, reading);
        (await ResolveAsync(deviceDates, reading)).Should().Be(matched);

        var window = (await MetricsWindowsAsync(deviceDates))[matched!.Value];
        reading.Should().BeOnOrAfter(window.Start!.Value);
        if (window.End is { } end)
            reading.Should().BeBefore(end);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(20)]
    [InlineData(22)]
    public async Task IngestAndTheAidMetrics_PutAReadingOnTheSameDevice_InTheHoursBeforeATripEntryAtLocalMidnight(int hour)
    {
        var beforeTheTrip = Cgm(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 10));
        var afterTheTrip = Cgm(new DateOnly(2026, 5, 11), null);
        _patientDevices
            .Setup(r => r.GetByDeviceIdAsync(_deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([beforeTheTrip, afterTheTrip]);
        _patientDevices
            .Setup(r => r.GetByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([beforeTheTrip, afterTheTrip]);
        var deviceDates = DeviceDateClockDoubles.Resolver(
            timeline:
            [
                new TimezoneTimelineEntry { EffectiveFrom = DateTime.MinValue, Timezone = "Europe/London" },
                new TimezoneTimelineEntry { EffectiveFrom = new DateTime(2026, 5, 11), Timezone = "Asia/Tokyo" },
            ]);
        var reading = new DateTime(2026, 5, 10, hour, 0, 0, DateTimeKind.Utc);

        var matched = await StampAsync(deviceDates, reading);
        (await ResolveAsync(deviceDates, reading)).Should().Be(matched);

        var window = (await MetricsWindowsAsync(
            deviceDates, new DateTime(2026, 4, 25, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc)))[matched!.Value];
        reading.Should().BeOnOrAfter(window.Start!.Value);
        if (window.End is { } end)
            reading.Should().BeBefore(end);
        matched.Should().Be(afterTheTrip.Id, "the wearer's 11 May began at 15:00Z");
    }

    private Task<Guid?> ResolveAsync(DeviceDateClockResolver deviceDates, DateTime reading) =>
        new DeviceService(Mock.Of<IDeviceRepository>(), _patientDevices.Object, MockTenantAccessor.Create().Object, deviceDates)
            .ResolvePatientDeviceAsync(_deviceId, new DateTimeOffset(reading).ToUnixTimeMilliseconds());

    private async Task<Guid?> StampAsync(DeviceDateClockResolver deviceDates, DateTime reading)
    {
        var record = new SensorGlucose { Mgdl = 120, Timestamp = reading };
        await new PatientDeviceStamper(_patientDevices.Object, deviceDates, NullLogger<PatientDeviceStamper>.Instance)
            .StampAsync([record], [DeviceCategory.CGM], batchSource: null);
        return record.PatientDeviceId;
    }

    private async Task<Dictionary<Guid, CgmDeviceWindow>> MetricsWindowsAsync(
        DeviceDateClockResolver deviceDates, DateTime? from = null, DateTime? to = null)
    {
        IReadOnlyCollection<CgmDeviceWindow>? windows = null;
        var statistics = new Mock<IStatisticsService>();
        statistics
            .Setup(s => s.CalculateCgmActivePercent(
                It.IsAny<IEnumerable<SensorGlucose>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<IReadOnlyCollection<CgmDeviceWindow>?>()))
            .Callback((IEnumerable<SensorGlucose> _, DateTime? _, DateTime? _, IReadOnlyCollection<CgmDeviceWindow>? d) => windows = d)
            .Returns((double?)null);

        var aid = new Mock<IAidMetricsService>();
        aid.Setup(a => a.Calculate(
                It.IsAny<IReadOnlyList<DeviceSegmentInput>>(), It.IsAny<IReadOnlyList<ApsLoopCycle>>(),
                It.IsAny<IReadOnlyList<TempBasal>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .Returns(new AidSystemMetrics());

        var snapshots = new Mock<IApsSnapshotRepository>();
        snapshots
            .Setup(r => r.GetLoopCyclesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await new AidSystemMetricsReader(
            _patientDevices.Object,
            snapshots.Object,
            Mock.Of<ITempBasalRepository>(),
            Mock.Of<IDeviceEventRepository>(),
            Mock.Of<ISensorGlucoseRepository>(),
            CanonicalGlucosePassThrough.Create(),
            Mock.Of<ITargetRangeScheduleRepository>(),
            Mock.Of<IActiveProfileResolver>(),
            statistics.Object,
            aid.Object,
            deviceDates,
            TimeProvider.System).ReadAsync(
                from ?? new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc),
                to ?? new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc),
                CancellationToken.None);

        return windows!.ToDictionary(w => w.DeviceId);
    }

    private PatientDevice Cgm(DateOnly start, DateOnly? end) => new()
    {
        Id = Guid.CreateVersion7(),
        DeviceId = _deviceId,
        DeviceCategory = DeviceCategory.CGM,
        StartDate = start,
        EndDate = end,
    };
}
