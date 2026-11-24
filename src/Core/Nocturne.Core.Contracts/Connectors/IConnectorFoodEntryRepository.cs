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
    /// Dismisses a pending food entry as <see cref="ConnectorFoodEntryStatus.Standalone"/> with one
    /// update conditional on it still being pending, the claim <see cref="AcceptMatchAsync"/> makes, so
    /// an entry an accept has linked is left as it is.
    /// </summary>
    /// <remarks>
    /// An entry already dismissed is <see cref="DismissMatchOutcome.AlreadyDismissed"/>. One matched or
    /// withdrawn is <see cref="DismissMatchOutcome.AlreadyResolved"/>.
    /// </remarks>
    Task<DismissMatchOutcome> DismissAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Links a pending food entry to a carb intake: claims the entry and writes its
    /// <see cref="TreatmentFood"/> row in one transaction, so two accepts racing for one entry write
    /// one row and only the winner reports <see cref="AcceptMatchOutcome.Linked"/>.
    /// </summary>
    /// <remarks>
    /// An entry already matched to <see cref="TreatmentFood.CarbIntakeId"/> is
    /// <see cref="AcceptMatchOutcome.AlreadyLinked"/>. One matched to another carb intake or to none
    /// on record, dismissed or withdrawn is <see cref="AcceptMatchOutcome.AlreadyResolved"/>.
    /// </remarks>
    /// <param name="foodEntryId">The food entry to link.</param>
    /// <param name="treatmentFood">The row to write when this call wins the claim. Its id must be set.</param>
    Task<AcceptMatchOutcome> AcceptMatchAsync(
        Guid foodEntryId,
        TreatmentFood treatmentFood,
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
    /// Imports of one connector source and trimmed external entry id are one entry, written from the
    /// first of them; the others, and the food they carry, are ignored.
    /// </remarks>
    /// <param name="imports">Imports with no connector source or no external entry id are skipped.</param>
    Task<ConnectorFoodEntryUpsert> UpsertAsync(
        IReadOnlyList<ConnectorFoodEntryImport> imports,
        CancellationToken ct = default);

    /// <summary>
    /// Marks the pending entries of <paramref name="connectorSource"/> consumed in the window and
    /// absent from <paramref name="presentExternalEntryIds"/> as <see cref="ConnectorFoodEntryStatus.Deleted"/>.
    /// Each is claimed with an update conditional on it still being pending, so an entry matched or
    /// dismissed since the read is left alone and is not returned.
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
