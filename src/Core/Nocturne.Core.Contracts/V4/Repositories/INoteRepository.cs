using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

public interface INoteRepository
{
    Task<IEnumerable<Note>> GetAsync(
        long? from,
        long? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        CancellationToken ct = default
    );
    Task<Note?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Note?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);
    Task<Note> CreateAsync(Note model, CancellationToken ct = default);
    Task<Note> UpdateAsync(Guid id, Note model, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);
    Task<int> CountAsync(long? from, long? to, CancellationToken ct = default);
    Task<IEnumerable<Note>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    );
    Task<IEnumerable<Note>> BulkCreateAsync(
        IEnumerable<Note> records,
        CancellationToken ct = default
    );
}
