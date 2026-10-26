using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Glucose;

/// <summary>
/// Service for managing compression low suggestions.
/// </summary>
/// <seealso cref="ICompressionLowDetectionService"/>
/// <seealso cref="IStateSpanService"/>
public interface ICompressionLowService
{
    /// <param name="status">Filter by <see cref="CompressionLowStatus"/>, or null to return all statuses.</param>
    /// <param name="nightOf">Filter to a specific night, or null to return all nights.</param>
    /// <returns>Matching compression-low suggestions.</returns>
    Task<IEnumerable<CompressionLowSuggestion>> GetSuggestionsAsync(
        CompressionLowStatus? status = null,
        DateOnly? nightOf = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a single suggestion with glucose entries for charting
    /// </summary>
    Task<CompressionLowSuggestionWithEntries?> GetSuggestionWithEntriesAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Accept a suggestion with adjusted bounds, creating a DataExclusion StateSpan that marks the
    /// range. Nothing filters on the span yet, so the readings still count in every statistic.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the suggestion is not found or is not pending.</exception>
    Task<StateSpan> AcceptSuggestionAsync(
        Guid id,
        long startMills,
        long endMills,
        CancellationToken cancellationToken = default,
        bool demoSeed = false);

    /// <exception cref="InvalidOperationException">Thrown when the suggestion is not found or is not pending.</exception>
    Task DismissSuggestionAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply one action to several suggestions. Every item is checked before any is written, and
    /// one that fails the check rejects the whole batch untouched.
    /// </summary>
    /// <remarks>
    /// The writes then run one after another and are not one database commit: a span, a suggestion
    /// row and a notification are written by services that each open their own context, so a
    /// failure part way leaves the earlier items applied. A retry finishes the rest: a suggestion
    /// already in the state the action leaves it in is skipped, and deleting one that is already
    /// gone does nothing. An accept that wrote its span but failed before the suggestion recorded
    /// it writes the span again on retry.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown before any write when the action is unknown, or an item is not found or not in a state the
    /// action applies to; and part way through when a suggestion changes under the batch.
    /// </exception>
    Task ApplyBatchAsync(
        CompressionLowBatchAction action,
        IReadOnlyCollection<CompressionLowBatchItem> items,
        CancellationToken cancellationToken = default);
}
