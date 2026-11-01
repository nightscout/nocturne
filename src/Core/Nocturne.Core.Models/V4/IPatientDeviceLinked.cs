namespace Nocturne.Core.Models.V4;

/// <summary>A record that can carry a link to the <see cref="PatientDevice"/> it came from.</summary>
/// <remarks>
/// Every <see cref="IDeviceAttributed"/> record is one; so are the APS and pump snapshots, which take
/// their link from their pump device rather than from a usage-window match.
/// </remarks>
public interface IPatientDeviceLinked
{
    /// <summary>Foreign key to the <see cref="PatientDevice"/> this record is attributed to.</summary>
    Guid? PatientDeviceId { get; set; }
}
