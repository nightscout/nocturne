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

    [Theory]
    [InlineData("AUTO_BASAL", "Automatic")]
    [InlineData("SAFE_BASAL", "Automatic")]
    [InlineData("MANUAL", "Manual")]
    [InlineData("SOME_UNKNOWN_STATE", "Manual")]
    public void Map_SetsPumpMode_FromTherapyAlgorithmShieldState(string shieldState, string expectedMode)
    {
        var data = CreatePumpData();
        data.TherapyAlgorithmState = new CareLinkTherapyAlgorithmState { AutoModeShieldState = shieldState };

        var result = CareLinkDeviceStatusMapper.Map(data);

        result!.Pump!.PumpMode.Should().Be(expectedMode);
    }

    [Fact]
    public void Map_LeavesPumpModeNull_WhenNoTherapyAlgorithmState()
    {
        var data = CreatePumpData();

        var result = CareLinkDeviceStatusMapper.Map(data);

        result!.Pump!.PumpMode.Should().BeNull();
    }

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
    public void Map_CarriesSensorLifeCalibrationAndTransmitterBatteryOnTheCgm()
    {
        var data = CreatePumpData();
        data.CgmInfo = new CareLinkCgmInfo { SensorType = "DURABLE" };
        data.SensorDurationMinutes = 6266;
        data.CalibStatus = "LESS_THAN_TWELVE_HRS";
        data.GstBatteryLevel = 92;

        var cgm = CareLinkDeviceStatusMapper.Map(data)!.Cgm!;

        cgm.SessionTimeRemaining.Should().Be(6266);
        cgm.CalibrationStatus.Should().Be("LESS_THAN_TWELVE_HRS");
        cgm.TransmitterBattery.Should().Be(92);
    }

    [Theory]
    [InlineData(65535, null)]
    [InlineData(-1, null)]
    [InlineData(0, 0)]
    public void Map_ReadsSensorLifeLeft_WithTheUnknownMarkerAsNoReading(int minutes, int? expected)
    {
        var data = CreatePumpData();
        data.CgmInfo = new CareLinkCgmInfo { SensorType = "DURABLE" };
        data.SensorDurationMinutes = minutes;

        CareLinkDeviceStatusMapper.Map(data)!.Cgm!.SessionTimeRemaining.Should().Be(expected);
    }

    [Fact]
    public void Map_KeepsReservoirPercentAsExtendedPumpData_WithoutDeviceInformation()
    {
        var data = CreatePumpData();
        data.ReservoirLevelPercent = 33;

        var extended = CareLinkDeviceStatusMapper.Map(data)!.Pump!.Extended;

        extended.Should().ContainKey("reservoirLevelPercent").WhoseValue.Should().Be(33);
    }

    [Fact]
    public void Map_CarriesLinkStatesAndPhoneBatteryStatusOnTheConnectBlock()
    {
        var data = CreatePumpData();
        data.PumpCommunicationState = true;
        data.GstCommunicationState = false;
        data.ConduitBatteryStatus = "HIGH";

        var connect = System.Text.Json.JsonSerializer.SerializeToElement(CareLinkDeviceStatusMapper.Map(data)!.Connect);

        connect.GetProperty("pumpCommunicationState").GetBoolean().Should().BeTrue();
        connect.GetProperty("gstCommunicationState").GetBoolean().Should().BeFalse();
        connect.GetProperty("conduitBatteryStatus").GetString().Should().Be("HIGH");
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

    [Fact]
    public void Map_NonGuardian_PopulatesPumpModelAndSerialForRegistration()
    {
        var data = CreatePumpData();
        data.PumpModelNumber = "MMT-1885";
        data.MedicalDeviceSerialNumber = "NG4304436H";

        var result = CareLinkDeviceStatusMapper.Map(data);

        result!.Pump!.Model.Should().Be("MMT-1885");
        result.Pump.Serial.Should().Be("NG4304436H");
    }

    [Fact]
    public void Map_WithSensorType_RegistersCgmWithGuardianModel()
    {
        var data = CreatePumpData();
        data.SensorState = "NO_ERROR_MESSAGE";
        data.CgmInfo = new CareLinkCgmInfo { SensorType = "DURABLE" };

        var result = CareLinkDeviceStatusMapper.Map(data);

        result!.Cgm.Should().NotBeNull();
        result.Cgm!.Manufacturer.Should().Be("Medtronic");
        result.Cgm.Model.Should().Be("Guardian (DURABLE)");
    }

    [Fact]
    public void Map_WithSensorStateButNoCgmInfo_RegistersGenericGuardian()
    {
        var data = CreatePumpData();
        data.SensorState = "NO_ERROR_MESSAGE";

        var result = CareLinkDeviceStatusMapper.Map(data);

        result!.Cgm.Should().NotBeNull();
        result.Cgm!.Model.Should().Be("Guardian");
    }

    [Fact]
    public void Map_WithNoSensorPresent_OmitsCgm()
    {
        var data = CreatePumpData();
        data.SensorState = null;
        data.CgmInfo = null;

        var result = CareLinkDeviceStatusMapper.Map(data);

        result!.Cgm.Should().BeNull();
    }

    [Fact]
    public void Map_WithSuspendedFlag_SetsPumpSuspended()
    {
        var data = CreatePumpData();
        data.MedicalDeviceSuspended = true;

        var result = CareLinkDeviceStatusMapper.Map(data);

        result!.Pump!.Status!.Suspended.Should().BeTrue();
    }

    [Fact]
    public void Map_WithGstBattery_SetsCgmTransmitterBattery()
    {
        var data = CreatePumpData();
        data.SensorState = "NO_ERROR_MESSAGE";
        data.GstBatteryLevel = 71;

        var result = CareLinkDeviceStatusMapper.Map(data);

        result!.Cgm!.TransmitterBattery.Should().Be(71);
    }

    [Fact]
    public void Map_WithDeviceInformation_PrefersModelNumberAndCapturesFirmware()
    {
        var data = CreatePumpData();
        data.PumpModelNumber = "MMT-1885";
        data.MedicalDeviceInformation = new CareLinkMedicalDeviceInformation
        {
            ModelNumber = "MMT-1885",
            FirmwareRevision = "18.12.3",
            HardwareRevision = "A2.01",
        };

        var result = CareLinkDeviceStatusMapper.Map(data);

        result!.Pump!.Model.Should().Be("MMT-1885");
        result.Pump.Extended.Should().ContainKey("firmwareRevision");
        result.Pump.Extended!["firmwareRevision"].Should().Be("18.12.3");
    }
}
