using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository port for the patient's own record, which holds profile-level metadata
/// such as name, date of birth, and preferred units.
/// </summary>
/// <remarks>
/// There is exactly one <see cref="PatientRecord"/> per tenant. <see cref="GetOrCreateAsync"/>
/// should be preferred over <see cref="GetAsync"/> when a caller requires a non-null result,
/// as it materialises a default record on first access.
/// </remarks>
/// <seealso cref="PatientRecord"/>
public interface IPatientRecordRepository
{
    /// <summary>Returns the patient record for the current tenant, or null if none exists.</summary>
    Task<PatientRecord?> GetAsync(CancellationToken ct = default);

    /// <summary>Returns the patient record for the current tenant, creating a default one if none exists.</summary>
    Task<PatientRecord> GetOrCreateAsync(CancellationToken ct = default);

    /// <summary>Updates the patient record and returns the saved result.</summary>
    Task<PatientRecord> UpdateAsync(PatientRecord model, WriteOrigin origin, CancellationToken ct = default);
}
