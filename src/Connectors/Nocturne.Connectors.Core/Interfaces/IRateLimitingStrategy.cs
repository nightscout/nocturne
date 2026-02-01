namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     Interface for implementing rate limiting strategies
/// </summary>
public interface IRateLimitingStrategy
{
    /// <summary>
    ///     Apply rate limiting delay between API calls
    /// </summary>
    /// <param name="requestIndex">Zero-based index of the current request in a sequence</param>
    Task ApplyDelayAsync(int requestIndex);
}