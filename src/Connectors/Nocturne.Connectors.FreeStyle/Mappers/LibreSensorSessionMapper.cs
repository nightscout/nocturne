using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.FreeStyle.Mappers;

/// <summary>The sensor start LibreLinkUp reports for the connection's current sensor, at its activation time.</summary>
public static class LibreSensorSessionMapper
{
    /// <summary>
    /// One start per sensor, keyed on its serial, so every sync upserts the same record. The serial
    /// goes in <see cref="V4RecordBase.Device"/> as well, where device attribution matches serials.
    /// </summary>
    /// <returns>The start, or null when the connection names no activated sensor.</returns>
    public static DeviceEvent? ToSensorStart(LibreSensor? sensor, DateTime now)
    {
        if (sensor?.SerialNumber is not { Length: > 0 } serial || sensor.ActivatedAt is not { } activatedAt || activatedAt <= 0)
            return null;

        return new DeviceEvent
        {
            Id = Guid.CreateVersion7(),
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(activatedAt).UtcDateTime,
            EventType = DeviceEventType.SensorStart,
            Device = $"{LibreLinkUpConstants.Configuration.DeviceIdentifier} {serial}",
            DataSource = DataSources.LibreConnector,
            SyncIdentifier = $"libre_sensor_{serial}",
            CreatedAt = now,
            ModifiedAt = now,
        };
    }
}
