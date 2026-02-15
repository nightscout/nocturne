using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Identifies which APS system produced a device status snapshot
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApsSystem>))]
public enum ApsSystem
{
    /// <summary>
    /// OpenAPS, AAPS, or Trio/FreeAPS X (all use the "openaps" key in devicestatus)
    /// </summary>
    OpenAps,

    /// <summary>
    /// Loop for iOS
    /// </summary>
    Loop
}
