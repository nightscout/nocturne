using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<SyncPhase>))]
public enum SyncPhase
{
    Syncing,
    Completed,
    Failed
}
