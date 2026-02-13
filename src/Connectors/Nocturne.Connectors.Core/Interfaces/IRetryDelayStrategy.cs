namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     Interface for retry delay strategies used by connectors
/// </summary>
public interface IRetryDelayStrategy
{
    /// <summary>
    ///     Apply a delay before retrying a failed operation
    /// </summary>
    /// <param name="attemptNumber">The attempt number (0-based)</param>
    Task ApplyRetryDelayAsync(int attemptNumber);
}