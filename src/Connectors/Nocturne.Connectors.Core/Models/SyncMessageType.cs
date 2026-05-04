using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SyncMessageType
{
    Authenticating,
    FetchingData,
    FetchingDataType,
    ProcessingDataType,
    PublishingDataType,
    SyncComplete,
    SyncFailed
}
