using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// Unit of measurement for glucose values
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GlucoseUnit>))]
public enum GlucoseUnit
{
    MgDl,
    Mmol
}
