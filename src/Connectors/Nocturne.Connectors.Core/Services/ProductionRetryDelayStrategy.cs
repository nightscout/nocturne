using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Exponential backoff from 30 seconds, capped at 2 minutes so no single delay outlasts
///     <c>ConnectorBackgroundService.PerTenantSyncTimeout</c> (3 minutes); the token ends a delay
///     the timeout overtakes.
/// </summary>
public class ProductionRetryDelayStrategy : IRetryDelayStrategy
{
    internal static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(2);

    public async Task ApplyRetryDelayAsync(int attemptNumber, CancellationToken cancellationToken)
    {
        await Task.Delay(DelayFor(attemptNumber), cancellationToken);
    }

    internal static TimeSpan DelayFor(int attemptNumber)
    {
        var exponential = BaseDelay.TotalMilliseconds * Math.Pow(2, attemptNumber);
        return TimeSpan.FromMilliseconds(Math.Min(exponential, MaxDelay.TotalMilliseconds));
    }
}