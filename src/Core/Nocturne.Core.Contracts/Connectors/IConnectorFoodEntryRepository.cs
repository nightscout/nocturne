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

    /// <summary>
    /// Upserts the foods the imports carry and the entries that reference them in one commit, so a
    /// failure leaves neither half written.
    /// </summary>
    /// <remarks>
    /// A food is matched on connector source and external id. A food the user deleted stays deleted
    /// and its entries are not linked to it; one a system sweep deleted is restored on its own row.
    /// Foods are reported to <c>ILegacyReadCacheInvalidation</c> after the commit when an existing
    /// one changed, since a legacy treatment is served with the name of its food.
    /// </remarks>
    /// <param name="imports">Imports with no connector source or no external entry id are skipped.</param>
    Task<ConnectorFoodEntryUpsert> UpsertAsync(
        IReadOnlyList<ConnectorFoodEntryImport> imports,
        CancellationToken ct = default);

    /// <summary>
    /// Marks the pending entries of <paramref name="connectorSource"/> consumed in the window and
    /// absent from <paramref name="presentExternalEntryIds"/> as <see cref="ConnectorFoodEntryStatus.Deleted"/>.
    /// Matched and dismissed entries are left alone.
    /// </summary>
    /// <param name="from">Start of the window, inclusive.</param>
    /// <param name="to">End of the window, inclusive.</param>
    /// <param name="resolvedAt">Stamped on every entry marked.</param>
    /// <returns>The ids of the entries marked.</returns>
    Task<IReadOnlyList<Guid>> MarkMissingAsDeletedAsync(
        string connectorSource,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlySet<string> presentExternalEntryIds,
        DateTimeOffset resolvedAt,
        CancellationToken ct = default);
}
