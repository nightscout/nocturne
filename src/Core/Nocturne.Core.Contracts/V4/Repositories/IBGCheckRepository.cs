using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

public interface IBGCheckRepository
{
    Task<IEnumerable<BGCheck>> GetAsync(
        long? from,
        long? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        CancellationToken ct = default
    );
    Task<BGCheck?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<BGCheck?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);
    Task<BGCheck> CreateAsync(BGCheck model, CancellationToken ct = default);
    Task<BGCheck> UpdateAsync(Guid id, BGCheck model, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);
    Task<int> CountAsync(long? from, long? to, CancellationToken ct = default);
    Task<IEnumerable<BGCheck>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    );
    Task<IEnumerable<BGCheck>> BulkCreateAsync(
        IEnumerable<BGCheck> records,
        CancellationToken ct = default
    );
}
