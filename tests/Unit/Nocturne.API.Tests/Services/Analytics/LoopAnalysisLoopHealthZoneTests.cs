using FluentAssertions;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.LoopAnalysis;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

public class LoopAnalysisLoopHealthZoneTests
{
    private static readonly TimeZoneInfo Adelaide = TimeZoneInfo.FindSystemTimeZoneById("Australia/Adelaide");

    [Fact]
    public async Task LoopHealth_ReadsADeviceWindowInTheDeviceDateZone()
    {
        var day = new DateOnly(2026, 6, 10);
        var cgm = new PatientDevice
        {
            Id = Guid.NewGuid(),
            DeviceCategory = DeviceCategory.CGM,
            StartDate = day,
            EndDate = day,
        };
        var devices = new Mock<IPatientDeviceRepository>();
        devices
            .Setup(r => r.GetByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([cgm]);

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

        var profiles = new Mock<IActiveProfileResolver>();
        profiles
            .Setup(r => r.GetProfileSpansForRangeAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var snapshots = new Mock<IApsSnapshotRepository>();
        snapshots
            .Setup(r => r.GetLoopCyclesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var timeline = new Mock<ITherapyTimelineResolver>();
        timeline
            .Setup(t => t.BuildAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TherapyTimeline([
                new TherapySegment(0, long.MaxValue,
                    new TherapySnapshot(1.0, TherapySnapshot.DefaultPeakMinutes, TherapySnapshot.DefaultCarbsPerHour,
                        null, null, 0, null, null, null)),
            ]));

        var series = new Mock<IBasalSeriesBuilder>();
        series
            .Setup(b => b.BuildAsync(
                It.IsAny<List<TempBasal>>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var spans = new Mock<IStateSpanRepository>();
        spans
            .Setup(r => r.GetByCategories(
                It.IsAny<IEnumerable<StateSpanCategory>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var aid = new Mock<IAidMetricsService>();
        aid.Setup(a => a.Calculate(
                It.IsAny<IReadOnlyList<DeviceSegmentInput>>(), It.IsAny<IReadOnlyList<ApsLoopCycle>>(),
                It.IsAny<IReadOnlyList<TempBasal>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .Returns(new AidSystemMetrics());

        var canonical = CanonicalGlucosePassThrough.Create();
        var deviceDates = DeviceDateClockDoubles.Resolver(profileZone: Adelaide.Id);
        var reader = new AidSystemMetricsReader(
            devices.Object,
            Mock.Of<IApsSnapshotRepository>(),
            Mock.Of<ITempBasalRepository>(),
            Mock.Of<IDeviceEventRepository>(),
            Mock.Of<ISensorGlucoseRepository>(),
            canonical,
            Mock.Of<ITargetRangeScheduleRepository>(),
            Mock.Of<IActiveProfileResolver>(),
            statistics.Object,
            aid.Object,
            deviceDates,
            TimeProvider.System);

        var service = new LoopAnalysisService(
            glucoseRepository: Mock.Of<ISensorGlucoseRepository>(),
            canonicalGlucose: canonical,
            bolusRepository: Mock.Of<IBolusRepository>(),
            carbIntakeRepository: Mock.Of<ICarbIntakeRepository>(),
            tempBasalRepository: Mock.Of<ITempBasalRepository>(),
            apsSnapshotRepository: snapshots.Object,
            stateSpanRepository: spans.Object,
            patientDeviceRepository: devices.Object,
            targetRangeScheduleRepository: Mock.Of<ITargetRangeScheduleRepository>(),
            therapyTimelineResolver: timeline.Object,
            therapySettingsResolver: Mock.Of<ITherapySettingsResolver>(),
            activeProfileResolver: profiles.Object,
            basalSeriesBuilder: series.Object,
            basalSegmentService: Mock.Of<IBasalSegmentService>(),
            iobCalculator: Mock.Of<IIobCalculator>(),
            cobCalculator: Mock.Of<ICobCalculator>(),
            therapySettingsRepository: Mock.Of<ITherapySettingsRepository>(),
            aidSystemMetrics: reader,
            deviceDates: deviceDates,
            deviceEventRepository: Mock.Of<IDeviceEventRepository>());

        await service.GetLoopHealthAsync(
            new LoopAnalysisFilter
            {
                From = new DateTime(2026, 6, 9, 14, 30, 0, DateTimeKind.Utc),
                To = new DateTime(2026, 6, 10, 14, 30, 0, DateTimeKind.Utc),
                Now = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc),
            },
            Adelaide);

        var window = windows.Should().ContainSingle().Subject;
        window.Start.Should().Be(new DateTime(2026, 6, 9, 14, 30, 0, DateTimeKind.Utc), "Adelaide is +9:30 in June");
        window.End.Should().Be(new DateTime(2026, 6, 10, 14, 30, 0, DateTimeKind.Utc));
    }
}
