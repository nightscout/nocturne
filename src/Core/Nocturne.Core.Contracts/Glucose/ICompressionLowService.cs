using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Glucose;

/// <summary>
/// Service for managing compression low suggestions.
/// </summary>
/// <seealso cref="ICompressionLowDetectionService"/>
/// <seealso cref="IStateSpanService"/>
public interface ICompressionLowService
{
    /// <summary>
    /// Get suggestions with optional filtering.
    /// </summary>
    /// <param name="status">Filter by <see cref="CompressionLowStatus"/>, or null to return all statuses.</param>
    /// <param name="nightOf">Filter to a specific night, or null to return all nights.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Dismiss a suggestion
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the suggestion is not found or is not pending.</exception>
    Task DismissSuggestionAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a suggestion and its associated state span if accepted
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the suggestion is not found.</exception>
    Task DeleteSuggestionAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
