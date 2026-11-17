using Nocturne.Connectors.CareLink.Models;
using Nocturne.Connectors.CareLink.Utilities;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.CareLink.Mappers;

public static class CareLinkDeviceStatusMapper
{
    private const string GuardianFamily = "Guardian";

    public static DeviceStatus? Map(CareLinkData? data, DateTime now)
    {
        if (data == null)
            return null;

        var pumpOffsetMs = CareLinkTimestampParser.CalculatePumpOffsetMs(
            data.MedicalDeviceTime ?? "",
            data.CurrentServerTime);

        var timestamp = CareLinkTimestampParser.ParseSgTimestamp(data.MedicalDeviceTime, pumpOffsetMs);
        var mills = timestamp.HasValue
            ? timestamp.Value.ToMills()
            : data.CurrentServerTime;

        var isGuardian = data.MedicalDeviceFamily?.Contains(GuardianFamily, StringComparison.OrdinalIgnoreCase) == true;
        var deviceName = $"CareLink {data.MedicalDeviceFamily ?? "Unknown"}";

        var status = new DeviceStatus
        {
            Id = Guid.CreateVersion7().ToString(),
            Mills = mills,
            Device = deviceName,
            CreatedAt = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
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
                Model = data.MedicalDeviceInformation?.ModelNumber ?? data.PumpModelNumber,
                Serial = data.MedicalDeviceSerialNumber,
                Status = data.MedicalDeviceSuspended.HasValue
                    ? new PumpStatusDetails { Suspended = data.MedicalDeviceSuspended }
                    : null,
                PumpMode = MapPumpMode(data.TherapyAlgorithmState),
                Extended = BuildPumpExtended(data),
            };

            status.Uploader = new UploaderStatus
            {
                Battery = data.ConduitBatteryLevel,
            };
        }

        var sensorModel = MapSensorModel(data);
        if (sensorModel != null)
        {
            status.Cgm = new CgmStatus
            {
                Manufacturer = "Medtronic",
                Model = sensorModel,
                SensorState = data.SensorState,
                TransmitterBattery = data.GstBatteryLevel,
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

    /// <summary>
    /// Derives a Medtronic CGM model name so the sensor can be registered as an in-use device.
    /// CareLink Connect exposes no per-sensor serial, so the reported sensor type identifies the
    /// Guardian sensor. Returns null when no sensor is present (no registration).
    /// </summary>
    private static string? MapSensorModel(CareLinkData data)
    {
        var sensorType = data.CgmInfo?.SensorType;
        if (!string.IsNullOrEmpty(sensorType))
            return $"Guardian ({sensorType})";

        return data.SensorState != null ? "Guardian" : null;
    }

    /// <summary>
    /// Maps the pump's closed-loop algorithm state to a canonical <see cref="PumpModeState"/> name.
    /// SmartGuard auto sub-states (<c>AUTO_BASAL</c>, <c>SAFE_BASAL</c>) are automatic delivery;
    /// <c>MANUAL</c> is open-loop. Returns null when the pump reports no algorithm state, so no
    /// pump-mode span is emitted (the signal is absent rather than "manual").
    /// </summary>
    /// <remarks>
    /// Conservative: any unrecognised shield state falls back to Manual rather than asserting
    /// automation. Only <c>autoModeShieldState</c> == <c>AUTO_BASAL</c> has been observed live; the
    /// other auto sub-states are mapped from Medtronic's documented SmartGuard states.
    /// </remarks>
    private static string? MapPumpMode(CareLinkTherapyAlgorithmState? state)
    {
        var shield = state?.AutoModeShieldState;
        if (string.IsNullOrWhiteSpace(shield))
            return null;

        return shield.ToUpperInvariant() switch
        {
            "AUTO_BASAL" or "SAFE_BASAL" => PumpModeState.Automatic.ToString(),
            _ => PumpModeState.Manual.ToString(),
        };
    }

    /// <summary>
    /// Captures the pump's firmware/hardware/software revisions as extended pump data so they
    /// surface on the pump snapshot. Returns null when the device reports no version information.
    /// </summary>
    private static Dictionary<string, object>? BuildPumpExtended(CareLinkData data)
    {
        var info = data.MedicalDeviceInformation;
        if (info == null)
            return null;

        var extended = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(info.FirmwareRevision))
            extended["firmwareRevision"] = info.FirmwareRevision;
        if (!string.IsNullOrEmpty(info.HardwareRevision))
            extended["hardwareRevision"] = info.HardwareRevision;
        if (!string.IsNullOrEmpty(info.SoftwareRevision))
            extended["softwareRevision"] = info.SoftwareRevision;

        return extended.Count > 0 ? extended : null;
    }
}
