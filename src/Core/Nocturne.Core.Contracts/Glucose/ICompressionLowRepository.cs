using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Glucose;

/// <summary>
/// Repository interface for <see cref="CompressionLowSuggestion"/> persistence operations.
/// </summary>
/// <seealso cref="CompressionLowSuggestion"/>
/// <seealso cref="ICompressionLowService"/>
/// <seealso cref="ICompressionLowDetectionService"/>
public interface ICompressionLowRepository
{
    /// <summary>
    /// Get <see cref="CompressionLowSuggestion"/> records with optional filtering.
    /// </summary>
    /// <param name="status">Optional <see cref="CompressionLowStatus"/> filter.</param>
    /// <param name="nightOf">Optional night date filter.</param>
    /// <returns>Matching suggestions.</returns>
    Task<IEnumerable<CompressionLowSuggestion>> GetSuggestionsAsync(
        CompressionLowStatus? status = null,
        DateOnly? nightOf = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a single <see cref="CompressionLowSuggestion"/> by ID.
    /// </summary>
    /// <param name="id">Suggestion ID.</param>
    /// <returns>The suggestion if found; <c>null</c> otherwise.</returns>
    Task<CompressionLowSuggestion?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist a new <see cref="CompressionLowSuggestion"/>.
    /// </summary>
    /// <returns>The created suggestion with assigned ID.</returns>
    Task<CompressionLowSuggestion> CreateAsync(
        CompressionLowSuggestion suggestion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist several new <see cref="CompressionLowSuggestion"/> records in one save.
    /// </summary>
    /// <param name="suggestions">Suggestions to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CreateManyAsync(
        IReadOnlyCollection<CompressionLowSuggestion> suggestions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update an existing <see cref="CompressionLowSuggestion"/>.
    /// </summary>
    /// <param name="suggestion">Suggestion with updated fields.</param>
    /// <returns>The updated suggestion, or <c>null</c> if not found.</returns>
    Task<CompressionLowSuggestion?> UpdateAsync(
        CompressionLowSuggestion suggestion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Count pending suggestions for a specific night.
    /// </summary>
    /// <param name="nightOf">The night date to count suggestions for.</param>
    /// <returns>Number of pending suggestions.</returns>
    Task<int> CountPendingForNightAsync(
        DateOnly nightOf,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if non-deleted suggestions already exist for a night.
    /// Only considers Pending and Accepted suggestions, allowing re-detection
    /// after all suggestions for a night have been dismissed or deleted.
    /// </summary>
    Task<bool> ActiveSuggestionsExistForNightAsync(
        DateOnly nightOf,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a suggestion of any status exists for a night, dismissed ones included.
    /// </summary>
    Task<bool> AnySuggestionExistsForNightAsync(
        DateOnly nightOf,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a <see cref="CompressionLowSuggestion"/> by ID.
    /// </summary>
    /// <param name="id">Suggestion ID to delete.</param>
    /// <returns><c>true</c> if deleted; <c>false</c> if not found.</returns>
    Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
