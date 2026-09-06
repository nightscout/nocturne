using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// A patient's biological (natal) sex. Used only where physiology matters — e.g. sex-specific
/// normative reference ranges for sleep-stage composition. This is distinct from gender identity
/// and from <see cref="PatientRecord.Pronouns"/>, which capture how the patient wishes to be
/// addressed; the two are recorded separately and neither is derived from the other.
/// </summary>
/// <seealso cref="PatientRecord"/>
[JsonConverter(typeof(JsonStringEnumConverter<BiologicalSex>))]
public enum BiologicalSex
{
    /// <summary>Female.</summary>
    Female,

    /// <summary>Male.</summary>
    Male
}
