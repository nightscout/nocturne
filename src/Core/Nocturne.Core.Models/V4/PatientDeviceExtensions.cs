namespace Nocturne.Core.Models.V4;

public static class PatientDeviceExtensions
{
    /// <summary>
    /// Best available human-readable name for a registered device: the catalog entry's name,
    /// falling back to the patient-supplied model then manufacturer.
    /// </summary>
    public static string DisplayName(this PatientDevice device) =>
        (device.CatalogId != null ? DeviceCatalog.GetById(device.CatalogId)?.Name : null)
        ?? device.Model
        ?? device.Manufacturer
        ?? "Unknown device";
}
