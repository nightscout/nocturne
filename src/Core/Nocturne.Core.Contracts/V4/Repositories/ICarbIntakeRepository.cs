using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

public interface ICarbIntakeRepository
{
    Task<IEnumerable<CarbIntake>> GetAsync(long? from, long? to, string? device, string? source, int limit = 100, int offset = 0, bool descending = true, CancellationToken ct = default);
    Task<CarbIntake?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<CarbIntake?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);
    Task<CarbIntake> CreateAsync(CarbIntake model, CancellationToken ct = default);
    Task<CarbIntake> UpdateAsync(Guid id, CarbIntake model, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);
    Task<int> CountAsync(long? from, long? to, CancellationToken ct = default);
    Task<IEnumerable<CarbIntake>> GetByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default);
}
