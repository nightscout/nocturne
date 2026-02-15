using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Production retry delay strategy with exponential backoff
///     Follows the legacy implementation with 2.5 minutes * 2^attempt
/// </summary>
public class ProductionRetryDelayStrategy : IRetryDelayStrategy
{
    public async Task ApplyRetryDelayAsync(int attemptNumber)
    {
        // Exponential backoff: 2.5 minutes * 2^attempt (following legacy implementation)
        var delayMs = (int)(2.5 * 60 * 1000 * Math.Pow(2, attemptNumber));
        await Task.Delay(delayMs);
    }
}