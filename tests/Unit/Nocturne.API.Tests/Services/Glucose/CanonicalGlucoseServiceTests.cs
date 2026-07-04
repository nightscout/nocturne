using FluentAssertions;
using Moq;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Glucose;

[Trait("Category", "Unit")]
public class CanonicalGlucoseServiceTests
{
    private readonly Mock<ISensorGlucoseRepository> _sgRepo = new();
    private readonly Mock<IPatientDeviceRepository> _deviceRepo = new();
    private readonly CanonicalGlucoseService _sut;

    private static readonly DateTime Now = DateTime.UtcNow;

    public CanonicalGlucoseServiceTests()
    {
        var demoMode = new Mock<Nocturne.API.Services.Platform.IDemoModeService>();
        demoMode.Setup(d => d.IsEnabled).Returns(false);
        _sut = new CanonicalGlucoseService(_sgRepo.Object, _deviceRepo.Object, demoMode.Object);
    }

    private static PatientDevice Cgm(int rank) => new()
    {
        Id = Guid.NewGuid(),
        DeviceCategory = DeviceCategory.CGM,
        Manufacturer = "Test",
        Model = "Test CGM",
        Rank = rank,
    };

    private static SensorGlucose Reading(Guid deviceId, double minutesAgo, double mgdl = 120) => new()
    {
        Id = Guid.NewGuid(),
        Mgdl = mgdl,
        Timestamp = Now.AddMinutes(-minutesAgo),
        PatientDeviceId = deviceId,
    };

    [Fact]
    public async Task SelectAsync_SingleStream_ReturnsInputWithoutLoadingDevices()
    {
        var deviceId = Guid.NewGuid();
        var readings = new List<SensorGlucose> { Reading(deviceId, 0), Reading(deviceId, 5) };

        var result = await _sut.SelectAsync(readings);

        result.Should().BeSameAs(readings);
        _deviceRepo.Verify(r => r.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SelectAsync_MultiStream_LoadsDevicesOnce_AndFiltersByRank()
    {
        var primary = Cgm(rank: 1);
        var secondary = Cgm(rank: 2);
        _deviceRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([primary, secondary]);

        var readings = new List<SensorGlucose> { Reading(primary.Id, 0), Reading(secondary.Id, 1) };

        var first = await _sut.SelectAsync(readings);
        var second = await _sut.SelectAsync(readings);

        first.Should().ContainSingle().Which.PatientDeviceId.Should().Be(primary.Id);
        second.Should().ContainSingle();
        _deviceRepo.Verify(r => r.GetAllAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetLatestAsync_ReturnsCanonicalLatest_NotRawLatest()
    {
        var primary = Cgm(rank: 1);
        var secondary = Cgm(rank: 2);
        _deviceRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([primary, secondary]);

        // Secondary reported most recently, but in a bucket where the primary also reported —
        // the canonical latest is the primary's reading. Pin both into the previous full
        // 5-minute bucket so the shared-bucket premise doesn't depend on the wall clock.
        var bucketStart = new DateTime(Now.Ticks - Now.Ticks % TimeSpan.FromMinutes(5).Ticks, DateTimeKind.Utc)
            .AddMinutes(-5);
        var primaryReading = Reading(primary.Id, 0, mgdl: 110);
        primaryReading.Timestamp = bucketStart.AddMinutes(1);
        var secondaryReading = Reading(secondary.Id, 0, mgdl: 160);
        secondaryReading.Timestamp = bucketStart.AddMinutes(2);
        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([secondaryReading, primaryReading]);

        var latest = await _sut.GetLatestAsync();

        latest.Should().NotBeNull();
        latest!.PatientDeviceId.Should().Be(primary.Id);
        latest.Mgdl.Should().Be(110);
    }

    [Fact]
    public async Task GetLatestAsync_NoRecentReadings_ReturnsNull()
    {
        _sgRepo.Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        (await _sut.GetLatestAsync()).Should().BeNull();
    }
}
