using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for managing compression low suggestions
/// </summary>
public interface ICompressionLowService
{
    /// <summary>
    /// Get suggestions with optional filtering
    /// </summary>
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
    /// Accept a suggestion with adjusted bounds, creating a DataExclusion StateSpan
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
