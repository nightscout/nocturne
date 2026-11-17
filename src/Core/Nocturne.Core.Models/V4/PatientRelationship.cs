using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// How the person who onboarded a tenant relates to its patient. Answered once per tenant; a
/// later answer (e.g. by a handover recipient) overwrites it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PatientRelationship>))]
public enum PatientRelationship
{
    /// <summary>The onboarder is the patient.</summary>
    Self,

    /// <summary>The onboarder cares for the patient (a parent, partner or child).</summary>
    Caregiver,

    /// <summary>The onboarder is setting the tenant up to hand it over to the patient.</summary>
    Helper,
}
