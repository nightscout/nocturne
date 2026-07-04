using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Devices;

[Trait("Category", "Unit")]
public class DeviceReattributionServiceTests
{
    private readonly Mock<ISensorGlucoseRepository> _repo = new();
    private readonly Mock<IPatientDeviceStamper> _stamper = new();
    private readonly DeviceReattributionService _service;

    public DeviceReattributionServiceTests()
    {
        _service = new DeviceReattributionService(
            _repo.Object, _stamper.Object, NullLogger<DeviceReattributionService>.Instance);
    }

    private static PatientDevice CgmDevice(DateOnly? start = null, DateOnly? end = null) => new()
    {
        Id = Guid.NewGuid(),
        DeviceCategory = DeviceCategory.CGM,
        Manufacturer = "Dexcom",
        Model = "G7",
        StartDate = start,
        EndDate = end,
    };

    [Fact]
    public async Task NonCgmDevice_ReturnsZero_AndTouchesNothing()
    {
        var pump = new PatientDevice
        {
            Id = Guid.NewGuid(),
            DeviceCategory = DeviceCategory.InsulinPump,
            Manufacturer = "Tandem",
            Model = "t:slim X2",
        };

        var result = await _service.ReattributeForDeviceAsync(pump);

        result.Should().Be(0);
        _repo.Verify(r => r.GetUnattributedAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _stamper.Verify(s => s.StampAsync(
            It.IsAny<IReadOnlyList<IDeviceAttributed>>(), It.IsAny<IReadOnlyList<DeviceCategory>>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.SetPatientDeviceIdsAsync(
            It.IsAny<IReadOnlyDictionary<Guid, Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CgmWithMatches_PersistsOnlyMatchedReadings_AndReturnsCount()
    {
        var device = CgmDevice(start: new DateOnly(2026, 6, 1), end: new DateOnly(2026, 6, 30));
        var matched = new SensorGlucose { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Mgdl = 120, DataSource = "dexcom" };
        var unmatched = new SensorGlucose { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Mgdl = 118, DataSource = "libre" };

        _repo.Setup(r => r.GetUnattributedAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SensorGlucose> { matched, unmatched });

        // Simulate the ingest ladder attributing only the matching reading.
        _stamper.Setup(s => s.StampAsync(
                It.IsAny<IReadOnlyList<IDeviceAttributed>>(), It.IsAny<IReadOnlyList<DeviceCategory>>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<IDeviceAttributed>, IReadOnlyList<DeviceCategory>, string?, CancellationToken>(
                (records, _, _, _) => matched.PatientDeviceId = device.Id)
            .Returns(Task.CompletedTask);

        IReadOnlyDictionary<Guid, Guid>? persisted = null;
        _repo.Setup(r => r.SetPatientDeviceIdsAsync(
                It.IsAny<IReadOnlyDictionary<Guid, Guid>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyDictionary<Guid, Guid>, CancellationToken>((map, _) => persisted = map)
            .ReturnsAsync(1);

        var result = await _service.ReattributeForDeviceAsync(device);

        result.Should().Be(1);
        persisted.Should().NotBeNull();
        persisted.Should().ContainKey(matched.Id).WhoseValue.Should().Be(device.Id);
        persisted.Should().NotContainKey(unmatched.Id);

        // Usage window is padded ±1 day around the device's local dates.
        _repo.Verify(r => r.GetUnattributedAsync(
            It.Is<DateTime?>(d => d == new DateOnly(2026, 6, 1).ToDateTime(TimeOnly.MinValue).AddDays(-1)),
            It.Is<DateTime?>(d => d == new DateOnly(2026, 6, 30).ToDateTime(TimeOnly.MaxValue).AddDays(1)),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CgmWithNoUnattributed_ReturnsZero_WithoutStampingOrPersisting()
    {
        var device = CgmDevice(start: new DateOnly(2026, 6, 1));
        _repo.Setup(r => r.GetUnattributedAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SensorGlucose>());

        var result = await _service.ReattributeForDeviceAsync(device);

        result.Should().Be(0);
        _stamper.Verify(s => s.StampAsync(
            It.IsAny<IReadOnlyList<IDeviceAttributed>>(), It.IsAny<IReadOnlyList<DeviceCategory>>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.SetPatientDeviceIdsAsync(
            It.IsAny<IReadOnlyDictionary<Guid, Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CgmWithUnattributedButNoMatches_ReturnsZero_WithoutPersisting()
    {
        var device = CgmDevice(start: new DateOnly(2026, 6, 1));
        var reading = new SensorGlucose { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Mgdl = 120, DataSource = "libre" };
        _repo.Setup(r => r.GetUnattributedAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SensorGlucose> { reading });

        // Stamper leaves the reading unattributed (no match against this device).
        _stamper.Setup(s => s.StampAsync(
                It.IsAny<IReadOnlyList<IDeviceAttributed>>(), It.IsAny<IReadOnlyList<DeviceCategory>>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _service.ReattributeForDeviceAsync(device);

        result.Should().Be(0);
        _repo.Verify(r => r.SetPatientDeviceIdsAsync(
            It.IsAny<IReadOnlyDictionary<Guid, Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
