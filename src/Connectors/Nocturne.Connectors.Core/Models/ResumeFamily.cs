namespace Nocturne.Connectors.Core.Models;

/// <summary>
///     A family of records that resumes from its own point: the newest record of the family the
///     connector has stored.
/// </summary>
public enum ResumeFamily
{
    Glucose,
    Treatments,
    StateSpans,
    DeviceStatus,
    Activity,
}
