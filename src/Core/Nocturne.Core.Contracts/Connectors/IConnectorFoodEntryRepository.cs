using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Connectors;

/// <seealso cref="IConnectorFoodEntryService"/>
public interface IConnectorFoodEntryRepository
{
    /// <returns>The matching food entry, or null if not found.</returns>
    Task<ConnectorFoodEntry?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <param name="from">Start of the time range (inclusive).</param>
    /// <param name="to">End of the time range (inclusive).</param>
    /// <returns>Food entries with pending status within the given range.</returns>
    Task<IReadOnlyList<ConnectorFoodEntry>> GetPendingInTimeRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);

    /// <param name="ids">The IDs to look up.</param>
    /// <returns>Food entries matching the given IDs.</returns>
    Task<IReadOnlyList<ConnectorFoodEntry>> GetByIdsAsync(
        IEnumerable<Guid> ids,
        CancellationToken ct = default);

    /// <summary>
    /// Update the status and matched treatment for a food entry.
    /// </summary>
    /// <param name="status">The new <see cref="ConnectorFoodEntryStatus"/>.</param>
    Task UpdateStatusAsync(
        Guid id,
        ConnectorFoodEntryStatus status,
        CancellationToken ct = default);
}
