using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

public interface IBolusCalculationRepository
{
    Task<IEnumerable<BolusCalculation>> GetAsync(long? from, long? to, string? device, string? source, int limit = 100, int offset = 0, bool descending = true, CancellationToken ct = default);
    Task<BolusCalculation?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<BolusCalculation?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);
    Task<BolusCalculation> CreateAsync(BolusCalculation model, CancellationToken ct = default);
    Task<BolusCalculation> UpdateAsync(Guid id, BolusCalculation model, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);
    Task<int> CountAsync(long? from, long? to, CancellationToken ct = default);
    Task<IEnumerable<BolusCalculation>> GetByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default);
}
