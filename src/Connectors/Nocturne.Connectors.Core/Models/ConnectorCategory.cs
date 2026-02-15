using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Core.Models;

/// <summary>
///     Categorizes the connector functionality.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectorCategory
{
    /// <summary>
    ///     Continuous Glucose Monitor (e.g., Dexcom, Libre)
    /// </summary>
    Cgm,

    /// <summary>
    ///     Insulin Pump (e.g., T:Connect, CareLink)
    /// </summary>
    Pump,

    /// <summary>
    ///     Nutrition / Food logging (e.g., MyFitnessPal)
    /// </summary>
    Nutrition,

    /// <summary>
    ///     Data Synchronization (e.g., Nightscout, Tidepool)
    /// </summary>
    Sync,

    /// <summary>
    ///     Other / Miscellaneous
    /// </summary>
    Other
}