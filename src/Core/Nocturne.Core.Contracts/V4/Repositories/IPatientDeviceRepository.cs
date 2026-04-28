using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository port for <see cref="PatientDevice"/> records that track which physical devices
/// (pump, CGM, meter) are associated with the patient and the time ranges they were in use.
/// </summary>
/// <seealso cref="PatientDevice"/>
/// <seealso cref="IDeviceRepository"/>
public interface IPatientDeviceRepository
{
    /// <summary>Returns all patient devices, including historical ones.</summary>
    Task<IEnumerable<PatientDevice>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Returns only the patient's currently active devices.</summary>
    Task<IEnumerable<PatientDevice>> GetCurrentAsync(CancellationToken ct = default);

    /// <summary>Returns devices that were active during the specified date range.</summary>
    Task<IEnumerable<PatientDevice>> GetByDateRangeAsync(DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>Returns all patient devices linked to the specified device.</summary>
    Task<IReadOnlyList<PatientDevice>> GetByDeviceIdAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>Returns a device by its ID, or null if not found.</summary>
    Task<PatientDevice?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Creates a new patient device record.</summary>
    Task<PatientDevice> CreateAsync(PatientDevice model, CancellationToken ct = default);

    /// <summary>Updates an existing patient device record.</summary>
    Task<PatientDevice> UpdateAsync(Guid id, PatientDevice model, CancellationToken ct = default);

    /// <summary>Deletes a patient device record by ID.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
