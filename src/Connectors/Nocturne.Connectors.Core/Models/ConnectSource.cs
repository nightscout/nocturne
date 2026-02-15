using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Core.Models;

/// <summary>
///     Represents the available data source types for connectors
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectSource
{
    Dexcom,
    LibreLinkUp,
    Glooko,
    MyLife,
    Tidepool,
    MyFitnessPal,
    Nightscout,
}
