using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// How the bolus calculation was determined
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CalculationType>))]
public enum CalculationType
{
    Suggested,
    Manual,
    Automatic
}
