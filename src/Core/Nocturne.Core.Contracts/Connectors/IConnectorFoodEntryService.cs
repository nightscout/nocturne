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
}
