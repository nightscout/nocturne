using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Connectors;

/// <summary>
/// Service interface for connector food entry imports.
/// </summary>
/// <seealso cref="IMealMatchingService"/>
/// <seealso cref="IConnectorFoodEntryRepository"/>
public interface IConnectorFoodEntryService
{
    /// <summary>
    /// Import connector food entries, deduplicating foods and entries as needed.
    /// </summary>
    /// <param name="userId">
    /// Subject the match suggestions are raised for. Null when the caller has no subject to
    /// attribute them to; the entries still import, without suggestions.
    /// </param>
    /// <param name="imports">The food entry imports to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<IReadOnlyList<ConnectorFoodEntry>> ImportAsync(
        string? userId,
        IEnumerable<ConnectorFoodEntryImport> imports,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Marks still-pending entries in a window <see cref="ConnectorFoodEntryStatus.Deleted"/> when the
    /// connector no longer reports them, withdrawing their match suggestions.
    /// </summary>
    /// <remarks>
    /// Deleting an entry is how a user corrects a mis-logged meal, and imports are an upsert, so
    /// without this the carbs and the suggestion outlive the entry. The caller must have read its
    /// source exhaustively: absence from a partial read is not evidence of a deletion, and passing an
    /// incomplete list withdraws every entry it omitted.
    /// </remarks>
    /// <param name="userId">
    /// Subject whose match suggestions are withdrawn alongside. Null when the caller has no subject;
    /// the entries are still retired.
    /// </param>
    /// <param name="connectorSource">Only entries from this connector are considered</param>
    /// <param name="from">Start of the window to reconcile, inclusive</param>
    /// <param name="to">End of the window to reconcile, inclusive</param>
    /// <param name="presentExternalEntryIds">Every external entry id the source still reports</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The number of entries marked deleted</returns>
    Task<int> MarkMissingAsDeletedAsync(
        string? userId,
        string connectorSource,
        DateTimeOffset from,
        DateTimeOffset to,
        IEnumerable<string> presentExternalEntryIds,
        CancellationToken cancellationToken = default
    );
}
