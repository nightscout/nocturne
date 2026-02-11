namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for detecting compression lows in overnight glucose data
/// </summary>
public interface ICompressionLowDetectionService
{
    /// <summary>
    /// Run detection for a specific night (for manual triggering/testing)
    /// </summary>
    /// <param name="nightOf">The night to analyze (date when sleep started)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of suggestions created</returns>
    Task<int> DetectForNightAsync(
        DateOnly nightOf,
        CancellationToken cancellationToken = default);
}
