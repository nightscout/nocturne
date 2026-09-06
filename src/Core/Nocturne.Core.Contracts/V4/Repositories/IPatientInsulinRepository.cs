using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository port for <see cref="PatientInsulin"/> records that describe the insulin formulations
/// prescribed to the patient, including basal and bolus designations.
/// </summary>
/// <seealso cref="PatientInsulin"/>
/// <seealso cref="PatientRecord"/>
public interface IPatientInsulinRepository
{
    /// <summary>Returns all insulin records for the patient, including discontinued ones.</summary>
    Task<IEnumerable<PatientInsulin>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Returns only the patient's currently prescribed insulins.</summary>
    Task<IEnumerable<PatientInsulin>> GetCurrentAsync(CancellationToken ct = default);

    /// <summary>Returns an insulin record by its ID, or null if not found.</summary>
    Task<PatientInsulin?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Creates a new patient insulin record.</summary>
    Task<PatientInsulin> CreateAsync(PatientInsulin model, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>Updates an existing patient insulin record.</summary>
    Task<PatientInsulin> UpdateAsync(Guid id, PatientInsulin model, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>Deletes a patient insulin record by ID.</summary>
    Task DeleteAsync(Guid id, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>Returns the insulin designated as the primary bolus insulin, or null if none is set.</summary>
    Task<PatientInsulin?> GetPrimaryBolusInsulinAsync(CancellationToken ct = default);

    /// <summary>Returns the insulin designated as the primary basal insulin, or null if none is set.</summary>
    Task<PatientInsulin?> GetPrimaryBasalInsulinAsync(CancellationToken ct = default);

    /// <summary>Designates an insulin as the primary for its category (basal or bolus), clearing the previous primary.</summary>
    Task SetPrimaryAsync(Guid insulinId, CancellationToken ct = default);
}
