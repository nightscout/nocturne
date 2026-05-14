using FluentAssertions;
using Nocturne.Connectors.CareLink.Mappers;
using Nocturne.Connectors.CareLink.Models;
using Xunit;

namespace Nocturne.Connectors.CareLink.Tests.Mappers;

public class CareLinkDeviceStatusMapperTests
{
    private static CareLinkData CreatePumpData() => new()
    {
        MedicalDeviceFamily = "BLE",
        MedicalDeviceBatteryLevelPercent = 85,
        ConduitBatteryLevel = 72,
        ReservoirRemainingUnits = 150.5,
        MedicalDeviceTime = "2024-01-15T14:30:00",
        CurrentServerTime = new DateTimeOffset(2024, 1, 15, 13, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
        ActiveInsulin = new CareLinkActiveInsulin { Amount = 2.5, Datetime = "2024-01-15T14:30:00" },
    };

    private static CareLinkData CreateGuardianData() => new()
    {
        MedicalDeviceFamily = "Guardian",
        MedicalDeviceBatteryLevelPercent = 90,
        ConduitBatteryLevel = 60,
        MedicalDeviceTime = "2024-01-15T14:30:00",
        CurrentServerTime = new DateTimeOffset(2024, 1, 15, 13, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
    };

    [Fact]
    public void Map_NonGuardian_PopulatesPumpStatusWithBatteryReservoirAndIob()
    {
        var data = CreatePumpData();
        var result = CareLinkDeviceStatusMapper.Map(data);

        result.Should().NotBeNull();
        result!.Pump.Should().NotBeNull();
        result.Pump!.Battery.Should().NotBeNull();
        result.Pump.Battery!.Percent.Should().Be(85);
        result.Pump.Reservoir.Should().Be(150.5);
        result.Pump.Iob.Should().NotBeNull();
        result.Pump.Iob!.Iob.Should().Be(2.5);
        result.Pump.Manufacturer.Should().Be("Medtronic");
    }

    [Fact]
    public void Map_NonGuardian_SetsUploaderBatteryFromConduit()
    {
        var data = CreatePumpData();
        var result = CareLinkDeviceStatusMapper.Map(data);

        result!.Uploader.Should().NotBeNull();
        result.Uploader!.Battery.Should().Be(72);
    }

    [Fact]
    public void Map_Guardian_OmitsPumpStatus()
    {
        var data = CreateGuardianData();
        var result = CareLinkDeviceStatusMapper.Map(data);

        result.Should().NotBeNull();
        result!.Pump.Should().BeNull();
    }

    [Fact]
    public void Map_Guardian_SetsUploaderBatteryFromMedicalDevice()
    {
        var data = CreateGuardianData();
        var result = CareLinkDeviceStatusMapper.Map(data);

        result!.Uploader.Should().NotBeNull();
        result.Uploader!.Battery.Should().Be(90);
    }

    [Fact]
    public void Map_SetsDeviceNameCorrectly()
    {
        var data = CreatePumpData();
        var result = CareLinkDeviceStatusMapper.Map(data);

        result!.Device.Should().Be("CareLink BLE");
    }

    [Fact]
    public void Map_ReturnsNull_WhenDataIsNull()
    {
        CareLinkDeviceStatusMapper.Map(null).Should().BeNull();
    }
}
