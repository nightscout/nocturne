using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

public interface IApsSnapshotRepository
{
    Task<IEnumerable<ApsSnapshot>> GetAsync(long? from, long? to, string? device, string? source, int limit = 100, int offset = 0, bool descending = true, CancellationToken ct = default);
    Task<ApsSnapshot?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ApsSnapshot?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);
    Task<ApsSnapshot> CreateAsync(ApsSnapshot model, CancellationToken ct = default);
    Task<ApsSnapshot> UpdateAsync(Guid id, ApsSnapshot model, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);
    Task<int> CountAsync(long? from, long? to, CancellationToken ct = default);
}
