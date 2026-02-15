using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SyncDataType
{
    Glucose,
    Treatments,
    Profiles,
    DeviceStatus,
    Activity,
    Food
}