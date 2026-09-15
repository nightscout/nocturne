using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Stores manually-entered lab HbA1c results, tenant-scoped and soft-deletable. See
/// <see cref="LabHbA1cResult"/> for why these never feed the eHbA1c calculation.
/// </summary>
public interface ILabHbA1cResultRepository
{
    /// <summary>Gets every non-deleted lab result for the current tenant, ordered by date.</summary>
    Task<IReadOnlyList<LabHbA1cResult>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Creates a new lab result.</summary>
    Task<LabHbA1cResult> CreateAsync(LabHbA1cResult result, CancellationToken ct = default);

    /// <summary>Soft-deletes a lab result. Returns false when no matching row exists.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
