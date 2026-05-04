using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Connectors;

/// <summary>
/// Repository interface for connector food entry operations.
/// </summary>
/// <seealso cref="IConnectorFoodEntryService"/>
public interface IConnectorFoodEntryRepository
{
    /// <summary>
    /// Get a food entry by ID.
    /// </summary>
    /// <param name="id">The food entry ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching food entry, or null if not found.</returns>
    Task<ConnectorFoodEntry?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Get pending food entries within a time range.
    /// </summary>
    /// <param name="from">Start of the time range (inclusive).</param>
    /// <param name="to">End of the time range (inclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Food entries with pending status within the given range.</returns>
    Task<IReadOnlyList<ConnectorFoodEntry>> GetPendingInTimeRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);

    /// <summary>
    /// Get food entries by IDs.
    /// </summary>
    /// <param name="ids">The IDs to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Food entries matching the given IDs.</returns>
    Task<IReadOnlyList<ConnectorFoodEntry>> GetByIdsAsync(
        IEnumerable<Guid> ids,
        CancellationToken ct = default);

    /// <summary>
    /// Update the status and matched treatment for a food entry.
    /// </summary>
    /// <param name="id">The food entry ID to update.</param>
    /// <param name="status">The new <see cref="ConnectorFoodEntryStatus"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateStatusAsync(
        Guid id,
        ConnectorFoodEntryStatus status,
        CancellationToken ct = default);
}
