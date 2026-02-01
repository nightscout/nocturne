using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Production rate limiting strategy with 5-second delays between requests
/// </summary>
public class ProductionRateLimitingStrategy(
    ILogger<ProductionRateLimitingStrategy> logger,
    int delayMs = 5000)
    : IRateLimitingStrategy
{
    public async Task ApplyDelayAsync(int requestIndex)
    {
        if (requestIndex > 0)
        {
            logger.LogDebug(
                "Rate limiting: waiting {DelayMs}ms before request {RequestIndex}", delayMs, requestIndex + 1
            );
            await Task.Delay(delayMs);
        }
    }
}