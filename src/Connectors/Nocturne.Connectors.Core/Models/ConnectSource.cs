using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Core.Models;

/// <summary>
/// Represents the available data source types for connectors
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectSource
{
    /// <summary>
    /// Dexcom Share data source
    /// </summary>
    Dexcom,

    /// <summary>
    /// FreeStyle LibreLinkUp data source
    /// </summary>
    LibreLinkUp,

    /// <summary>
    /// MiniMed CareLink data source
    /// </summary>
    CareLink,

    /// <summary>
    /// Glooko data source
    /// </summary>
    Glooko,

    /// <summary>
    /// Nightscout data source (for syncing between Nightscout instances)
    /// </summary>
    Nightscout,

    /// <summary>
    /// MyFitnessPal data source
    /// </summary>
    MyFitnessPal,

    /// <summary>
    /// Tidepool data source
    /// </summary>
    Tidepool,

    /// <summary>
    /// Tandem Source data source
    /// </summary>
    TConnectSync,

    MyLife,
}
