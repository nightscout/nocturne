using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Repository interface for connector food entry operations
/// </summary>
public interface IConnectorFoodEntryRepository
{
    /// <summary>
    /// Get a food entry by ID
    /// </summary>
    Task<ConnectorFoodEntry?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Get pending food entries within a time range
    /// </summary>
    Task<IReadOnlyList<ConnectorFoodEntry>> GetPendingInTimeRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);

    /// <summary>
    /// Get food entries by IDs
    /// </summary>
    Task<IReadOnlyList<ConnectorFoodEntry>> GetByIdsAsync(
        IEnumerable<Guid> ids,
        CancellationToken ct = default);

    /// <summary>
    /// Update the status and matched treatment for a food entry
    /// </summary>
    Task UpdateStatusAsync(
        Guid id,
        ConnectorFoodEntryStatus status,
        Guid? matchedTreatmentId,
        CancellationToken ct = default);
}
