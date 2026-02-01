using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Service interface for connector food entry imports.
/// </summary>
public interface IConnectorFoodEntryService
{
    /// <summary>
    /// Import connector food entries, deduplicating foods and entries as needed.
    /// </summary>
    /// <param name="userId">The user ID for meal matching notifications</param>
    /// <param name="imports">The food entry imports to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<IReadOnlyList<ConnectorFoodEntry>> ImportAsync(
        string userId,
        IEnumerable<ConnectorFoodEntryImport> imports,
        CancellationToken cancellationToken = default
    );
}
