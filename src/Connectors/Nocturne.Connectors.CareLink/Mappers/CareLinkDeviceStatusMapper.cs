using Nocturne.Connectors.CareLink.Models;
using Nocturne.Connectors.CareLink.Utilities;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.CareLink.Mappers;

public static class CareLinkDeviceStatusMapper
{
    private const string GuardianFamily = "Guardian";

    public static DeviceStatus? Map(CareLinkData? data)
    {
        if (data == null)
            return null;

        var pumpOffsetMs = CareLinkTimestampParser.CalculatePumpOffsetMs(
            data.MedicalDeviceTime ?? "",
            data.CurrentServerTime);

        var timestamp = CareLinkTimestampParser.ParseSgTimestamp(data.MedicalDeviceTime, pumpOffsetMs);
        var mills = timestamp.HasValue
            ? new DateTimeOffset(timestamp.Value, TimeSpan.Zero).ToUnixTimeMilliseconds()
            : data.CurrentServerTime;

        var isGuardian = data.MedicalDeviceFamily?.Contains(GuardianFamily, StringComparison.OrdinalIgnoreCase) == true;
        var deviceName = $"CareLink {data.MedicalDeviceFamily ?? "Unknown"}";

        var status = new DeviceStatus
        {
            Id = Guid.CreateVersion7().ToString(),
            Mills = mills,
            Device = deviceName,
            CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };

        if (isGuardian)
        {
            status.Uploader = new UploaderStatus
            {
                Battery = data.MedicalDeviceBatteryLevelPercent,
            };
        }
        else
        {
            status.Pump = new PumpStatus
            {
                Battery = new PumpBattery
                {
                    Percent = data.MedicalDeviceBatteryLevelPercent,
                },
                Reservoir = data.ReservoirRemainingUnits,
                Clock = data.MedicalDeviceTime,
                Iob = data.ActiveInsulin != null
                    ? new PumpIob
                    {
                        Iob = data.ActiveInsulin.Amount,
                        Timestamp = data.ActiveInsulin.Datetime,
                    }
                    : null,
                Manufacturer = "Medtronic",
            };

            status.Uploader = new UploaderStatus
            {
                Battery = data.ConduitBatteryLevel,
            };
        }

        status.Connect = new
        {
            conduitInRange = data.ConduitInRange,
            conduitMedicalDeviceInRange = data.ConduitMedicalDeviceInRange,
            conduitSensorInRange = data.ConduitSensorInRange,
            sensorState = data.SensorState,
        };

        return status;
    }
}
