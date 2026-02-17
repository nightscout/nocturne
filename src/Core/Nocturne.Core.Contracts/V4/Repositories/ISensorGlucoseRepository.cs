using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

public interface ISensorGlucoseRepository
{
    Task<IEnumerable<SensorGlucose>> GetAsync(
        long? from,
        long? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        CancellationToken ct = default
    );
    Task<SensorGlucose?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<SensorGlucose?> GetByLegacyIdAsync(string legacyId, CancellationToken ct = default);
    Task<SensorGlucose> CreateAsync(SensorGlucose model, CancellationToken ct = default);
    Task<SensorGlucose> UpdateAsync(Guid id, SensorGlucose model, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default);
    Task<int> CountAsync(long? from, long? to, CancellationToken ct = default);
    Task<IEnumerable<SensorGlucose>> GetByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken ct = default
    );
    Task<IEnumerable<SensorGlucose>> BulkCreateAsync(
        IEnumerable<SensorGlucose> records,
        CancellationToken ct = default
    );
    Task<DateTime?> GetLatestTimestampAsync(string? source = null, CancellationToken ct = default);
}
