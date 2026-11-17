namespace Nocturne.API.Models.Responses;

/// <summary>
/// The patient's IANA timezone as the API resolves it: the patient record's, else the active
/// therapy profile's. Null when neither names one.
/// </summary>
public class PatientTimezoneResponse
{
    /// <summary>The IANA timezone id, or null.</summary>
    public string? Timezone { get; set; }
}
