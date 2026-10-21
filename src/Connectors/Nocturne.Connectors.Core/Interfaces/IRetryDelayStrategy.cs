namespace Nocturne.Connectors.Core.Interfaces;

public interface IRetryDelayStrategy
{
    /// <param name="attemptNumber">0-based.</param>
    /// <param name="cancellationToken">Cancels the delay so a withdrawn run stops promptly.</param>
    Task ApplyRetryDelayAsync(int attemptNumber, CancellationToken cancellationToken);
}