using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Production rate limiting strategy with 5-second delays between requests
/// </summary>
public class ProductionRateLimitingStrategy : IRateLimitingStrategy
{
    private readonly int _delayMs;
    private readonly ILogger<ProductionRateLimitingStrategy> _logger;

    public ProductionRateLimitingStrategy(
        ILogger<ProductionRateLimitingStrategy> logger,
        int delayMs = 5000
    )
    {
        _logger = logger;
        _delayMs = delayMs;
    }

    public async Task ApplyDelayAsync(int requestIndex)
    {
        if (requestIndex > 0)
        {
            _logger.LogDebug(
                $"Rate limiting: waiting {_delayMs}ms before request {requestIndex + 1}"
            );
            await Task.Delay(_delayMs);
        }
    }
}