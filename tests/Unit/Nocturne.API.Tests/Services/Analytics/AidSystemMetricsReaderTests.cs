using FluentAssertions;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.AidDetection;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

[Trait("Category", "Unit")]
public class AidSystemMetricsReaderTests
{
    private static readonly DateTime WindowStart = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = WindowStart.AddHours(1);

    [Fact]
    public async Task ReadAsync_TempBasalStartedBeforeTheWindow_CountsItsInWindowPart()
    {
        var straddling = new TempBasal
        {
            StartTimestamp = WindowStart.AddHours(-1),
            EndTimestamp = WindowStart.AddMinutes(30),
            Origin = TempBasalOrigin.Algorithm,
            Rate = 0.5,
        };

        var metrics = await ReadAsync([straddling]);

        metrics.AidActivePercent.Should().Be(50.0);
        metrics.PumpUsePercent.Should().Be(50.0);
    }

    [Fact]
    public async Task ReadAsync_TempBasalOverAWeekBeforeTheWindow_IsNotCounted()
    {
        var stale = new TempBasal
        {
            StartTimestamp = WindowStart.AddDays(-8),
            EndTimestamp = WindowStart.AddDays(-7),
            Origin = TempBasalOrigin.Algorithm,
            Rate = 0.5,
        };

        var metrics = await ReadAsync([stale]);

        metrics.AidActivePercent.Should().BeNull();
    }

    private static async Task<AidSystemMetrics> ReadAsync(IReadOnlyList<TempBasal> stored)
    {
        var patientDevices = new Mock<IPatientDeviceRepository>();
        patientDevices
            .Setup(r => r.GetByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PatientDevice
            {
                Id = Guid.CreateVersion7(),
                DeviceCategory = DeviceCategory.InsulinPump,
                AidAlgorithm = AidAlgorithm.ControlIQ,
            }]);

        var snapshots = new Mock<IApsSnapshotRepository>();
        snapshots
            .Setup(r => r.GetLoopCyclesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var tempBasals = new Mock<ITempBasalRepository>();
        tempBasals
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? from, DateTime? to, string? _, string? _, int _, int _, bool _, CancellationToken _) =>
                stored.Where(t => t.StartTimestamp >= from && t.StartTimestamp <= to).ToList());

        var events = new Mock<IDeviceEventRepository>();
        events
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var glucose = new Mock<ISensorGlucoseRepository>();
        glucose
            .Setup(r => r.GetForAnalyticsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null, It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
            .ReturnsAsync([]);

        return await new AidSystemMetricsReader(
            patientDevices.Object,
            snapshots.Object,
            tempBasals.Object,
            events.Object,
            glucose.Object,
            CanonicalGlucosePassThrough.Create(),
            Mock.Of<ITargetRangeScheduleRepository>(),
            Mock.Of<IActiveProfileResolver>(),
            Mock.Of<IStatisticsService>(),
            new AidMetricsService([new TbrBasedStrategy()]),
            DeviceDateClockDoubles.Utc(),
            TimeProvider.System).ReadAsync(WindowStart, WindowEnd, CancellationToken.None);
    }
}
