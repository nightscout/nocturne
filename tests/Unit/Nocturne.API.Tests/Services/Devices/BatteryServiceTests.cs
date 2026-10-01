using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Devices;

public class BatteryServiceTests
{
    private const string Device = "openaps://phone";
    private const long Hour = 60 * 60 * 1000;

    private readonly Mock<IUploaderSnapshotRepository> _uploaderSnapshots = new();
    private readonly Mock<IPumpSnapshotRepository> _pumpSnapshots = new();
    private readonly BatteryService _service;

    public BatteryServiceTests()
    {
        _pumpSnapshots
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PumpSnapshot>());

        _service = new BatteryService(
            _uploaderSnapshots.Object,
            _pumpSnapshots.Object,
            NullLogger<BatteryService>.Instance);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetChargeCycles_TwoNightlyCharges_CountsOneCompletedCycle()
    {
        var t0 = 1_700_000_000_000L;
        GivenUploaderReadings(
            Reading(t0, 99, isCharging: false),
            Reading(t0 + (23 * Hour), 20, isCharging: false),
            Reading(t0 + (24 * Hour), 20, isCharging: true),
            Reading(t0 + (24 * Hour) + Hour, 99, isCharging: true),
            Reading(t0 + (32 * Hour), 99, isCharging: false),
            Reading(t0 + (32 * Hour) + Hour, 60, isCharging: false),
            Reading(t0 + (48 * Hour), 30, isCharging: true),
            Reading(t0 + (48 * Hour) + Hour, 99, isCharging: true));

        var cycles = (await _service.GetChargeCyclesAsync()).ToList();

        cycles.Should().HaveCount(1);
        cycles[0].Device.Should().Be(Device);
        cycles[0].IsComplete.Should().BeTrue();
        cycles[0].DischargeStartLevel.Should().Be(99);
        cycles[0].DischargeEndLevel.Should().Be(30);
        cycles[0].DischargeDurationMinutes.Should().Be(16 * 60);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetChargeCycles_ThreeNightlyCharges_CountsTwoCompletedCycles()
    {
        var t0 = 1_700_000_000_000L;
        GivenUploaderReadings(
            Reading(t0, 20, isCharging: true),
            Reading(t0 + (8 * Hour), 99, isCharging: false),
            Reading(t0 + (24 * Hour), 20, isCharging: true),
            Reading(t0 + (32 * Hour), 99, isCharging: false),
            Reading(t0 + (48 * Hour), 20, isCharging: true));

        var cycles = (await _service.GetChargeCyclesAsync()).ToList();

        cycles.Should().HaveCount(2);
        cycles.Should().OnlyContain(c => c.DischargeDurationMinutes.HasValue);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetChargeCycles_SingleChargeWithoutFollowingDischarge_CountsNoCycle()
    {
        var t0 = 1_700_000_000_000L;
        GivenUploaderReadings(
            Reading(t0, 50, isCharging: false),
            Reading(t0 + Hour, 50, isCharging: true),
            Reading(t0 + (2 * Hour), 99, isCharging: true));

        var cycles = await _service.GetChargeCyclesAsync();

        cycles.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetChargeCycles_RepeatedChargingReadings_DoNotDoubleCount()
    {
        var t0 = 1_700_000_000_000L;
        GivenUploaderReadings(
            Reading(t0, 20, isCharging: false),
            Reading(t0 + Hour, 30, isCharging: true),
            Reading(t0 + (2 * Hour), 60, isCharging: true),
            Reading(t0 + (3 * Hour), 99, isCharging: true),
            Reading(t0 + (4 * Hour), 99, isCharging: false),
            Reading(t0 + (6 * Hour), 40, isCharging: true));

        var cycles = (await _service.GetChargeCyclesAsync()).ToList();

        cycles.Should().HaveCount(1);
    }

    private void GivenUploaderReadings(params UploaderSnapshot[] snapshots) =>
        _uploaderSnapshots
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshots);

    private static UploaderSnapshot Reading(long mills, int battery, bool isCharging)
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;
        return new UploaderSnapshot
        {
            Id = Guid.NewGuid(),
            Device = Device,
            Timestamp = timestamp,
            CreatedAt = timestamp,
            Battery = battery,
            IsCharging = isCharging,
        };
    }
}
