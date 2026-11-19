using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Connectors;

/// <summary>
/// Imports food entries sourced from connectors (e.g. MyFitnessPal) and deduplicates them against
/// the existing food catalogue via <see cref="IMealMatchingService"/>. New food entries are created
/// for unmatched items; matched items are linked to the canonical food record.
/// </summary>
/// <seealso cref="IConnectorFoodEntryService"/>
public class ConnectorFoodEntryService : IConnectorFoodEntryService
{
    private readonly IConnectorFoodEntryRepository _entries;
    private readonly IMealMatchingService _mealMatchingService;
    private readonly TimeProvider _time;
    private readonly ILogger<ConnectorFoodEntryService> _logger;

    public ConnectorFoodEntryService(
        IConnectorFoodEntryRepository entries,
        IMealMatchingService mealMatchingService,
        TimeProvider timeProvider,
        ILogger<ConnectorFoodEntryService> logger
    )
    {
        _entries = entries ?? throw new ArgumentNullException(nameof(entries));
        _mealMatchingService = mealMatchingService ?? throw new ArgumentNullException(nameof(mealMatchingService));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<ConnectorFoodEntry>> ImportAsync(
        string? userId,
        IEnumerable<ConnectorFoodEntryImport> imports,
        CancellationToken cancellationToken = default
    )
    {
        var importList = imports?.ToList() ?? new List<ConnectorFoodEntryImport>();
        if (importList.Count == 0)
        {
            return Array.Empty<ConnectorFoodEntry>();
        }

        var upsert = await _entries.UpsertAsync(importList, cancellationToken);
        if (upsert.Entries.Count == 0)
        {
            return Array.Empty<ConnectorFoodEntry>();
        }

        // Matching raises notifications, which need a subject. The entries and the suggestion list
        // do not, so a tenant with no resolvable owner still gets both.
        if (upsert.EntriesToMatch.Count > 0 && !string.IsNullOrEmpty(userId))
        {
            try
            {
                await _mealMatchingService.ProcessNewFoodEntriesAsync(userId, upsert.EntriesToMatch, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process food entries for meal matching");
                // Don't fail the import if matching fails
            }
        }

        return upsert.Entries;
    }

    /// <inheritdoc />
    public async Task<int> MarkMissingAsDeletedAsync(
        string? userId,
        string connectorSource,
        DateTimeOffset from,
        DateTimeOffset to,
        IEnumerable<string> presentExternalEntryIds,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(connectorSource))
        {
            return 0;
        }

        var source = connectorSource.Trim();
        var present = presentExternalEntryIds?.ToHashSet(StringComparer.Ordinal)
                      ?? new HashSet<string>(StringComparer.Ordinal);

        var removed = await _entries.MarkMissingAsDeletedAsync(
            source, from, to, present, _time.GetUtcNow(), cancellationToken);
        if (removed.Count == 0)
        {
            return 0;
        }

        // The suggestion is a separate record from the entry and stays live until withdrawn.
        // A suggestion only exists against a subject, so with none there is nothing to withdraw.
        if (!string.IsNullOrEmpty(userId))
        {
            foreach (var entryId in removed)
            {
                try
                {
                    await _mealMatchingService.WithdrawSuggestionAsync(userId, entryId, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to archive the match notification for withdrawn food entry {FoodEntryId}",
                        entryId);
                }
            }
        }

        _logger.LogInformation(
            "Marked {Count} {ConnectorSource} food entries deleted after they disappeared upstream",
            removed.Count,
            source);

        return removed.Count;
    }
}
