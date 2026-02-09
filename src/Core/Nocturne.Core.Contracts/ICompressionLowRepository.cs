using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Repository interface for compression low suggestion operations
/// </summary>
public interface ICompressionLowRepository
{
    Task<IEnumerable<CompressionLowSuggestion>> GetSuggestionsAsync(
        CompressionLowStatus? status = null,
        DateOnly? nightOf = null,
        CancellationToken cancellationToken = default);

    Task<CompressionLowSuggestion?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<CompressionLowSuggestion> CreateAsync(
        CompressionLowSuggestion suggestion,
        CancellationToken cancellationToken = default);

    Task<CompressionLowSuggestion?> UpdateAsync(
        CompressionLowSuggestion suggestion,
        CancellationToken cancellationToken = default);

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

    Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}
