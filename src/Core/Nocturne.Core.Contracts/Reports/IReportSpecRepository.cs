using Nocturne.Core.Models.Reports;

namespace Nocturne.Core.Contracts.Reports;

/// <summary>
/// The tenant's saved custom reports, tenant-scoped and soft-deleted. Stores the spec as it is
/// given: validation and version rewrites belong to the API, which owns the spec format.
/// </summary>
public interface IReportSpecRepository
{
    /// <summary>The live custom reports, by name.</summary>
    Task<IReadOnlyList<SavedReportSpec>> ListAsync(CancellationToken ct = default);

    Task<SavedReportSpec?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>The number of live custom reports.</summary>
    Task<int> CountAsync(CancellationToken ct = default);

    Task<SavedReportSpecWrite> CreateAsync(
        string name, string specJson, int version, Guid? createdBySubjectId, CancellationToken ct = default);

    /// <summary>Renames the report, replaces its spec, or both; a null argument leaves that part as it is.</summary>
    Task<SavedReportSpecWrite> UpdateAsync(
        Guid id, string? name, string? specJson, int? version, CancellationToken ct = default);

    /// <summary>Soft-deletes the report. False when no live report has the id.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
