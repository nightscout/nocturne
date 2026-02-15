using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Type of insulin bolus delivery
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BolusType>))]
public enum BolusType
{
    Normal,
    Square,
    Dual
}
