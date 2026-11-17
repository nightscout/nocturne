using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Where a coefficient of variation falls against <see cref="Nocturne.Core.Constants.GlucoseConstants.CoefficientOfVariationTargetPercent"/>
/// and <see cref="Nocturne.Core.Constants.GlucoseConstants.CoefficientOfVariationVariablePercent"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GlycemicVariabilityStatus>))]
public enum GlycemicVariabilityStatus
{
    Stable,
    Moderate,
    Variable,
}
