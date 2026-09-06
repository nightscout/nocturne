using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     How one attempt inside <see cref="ConnectorRetryLoop"/> ended.
/// </summary>
public enum RetryStepDecision
{
    /// <summary>The run is over; the step's result is the run's result.</summary>
    Complete,

    /// <summary>Spend another attempt, after the delay for the attempt just made.</summary>
    RetryAfterDelay,

    /// <summary>Spend another attempt with no delay, the cause of the failure having been repaired.</summary>
    RetryImmediately
}

/// <summary>
///     The outcome of one attempt inside <see cref="ConnectorRetryLoop"/>.
/// </summary>
public readonly record struct RetryStep<T>(RetryStepDecision Decision, T? Result)
{
    public static RetryStep<T> Complete(T? result) => new(RetryStepDecision.Complete, result);

    public static RetryStep<T> RetryAfterDelay { get; } = new(RetryStepDecision.RetryAfterDelay, default);

    public static RetryStep<T> RetryImmediately { get; } = new(RetryStepDecision.RetryImmediately, default);
}

/// <summary>
///     The single attempt-sequencing loop shared by the connector base classes: it owns the attempt
///     budget, the delay between attempts and the cancellation check, while each caller's step
///     delegate owns what counts as success, what is retryable, and how failures are reported.
/// </summary>
public static class ConnectorRetryLoop
{
    /// <summary>
    ///     Runs <paramref name="step"/> until it completes or the attempt budget is spent.
    /// </summary>
    /// <param name="step">
    ///     Receives the 0-based attempt index and the clamped budget, and decides whether the run is
    ///     over. Exceptions it does not handle leave the loop.
    /// </param>
    /// <param name="retryDelayStrategy">Supplies the delay between attempts; never applied after the last one.</param>
    /// <param name="maxAttempts">
    ///     Total attempts, not retries on top of a first try, clamped to a floor of one.
    ///     Connectors pass a configured <see cref="IConnectorConfiguration.MaxRetryAttempts"/>, which
    ///     allows 0, and the operation has to run at least once for the call to mean anything.
    /// </param>
    /// <param name="onAttemptsExhausted">Produces the result once every attempt has been spent.</param>
    /// <param name="cancellationToken">Checked before each attempt.</param>
    /// <param name="onBeforeRetryDelay">Invoked with the just-finished attempt's index only when a delay follows it.</param>
    public static async Task<T?> RunAsync<T>(
        Func<int, int, Task<RetryStep<T>>> step,
        IRetryDelayStrategy retryDelayStrategy,
        int maxAttempts,
        Func<int, T?> onAttemptsExhausted,
        CancellationToken cancellationToken,
        Action<int>? onBeforeRetryDelay = null)
    {
        maxAttempts = Math.Max(1, maxAttempts);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await step(attempt, maxAttempts);

            if (outcome.Decision == RetryStepDecision.Complete)
                return outcome.Result;

            if (outcome.Decision == RetryStepDecision.RetryAfterDelay && attempt < maxAttempts - 1)
            {
                onBeforeRetryDelay?.Invoke(attempt);
                await retryDelayStrategy.ApplyRetryDelayAsync(attempt);
            }
        }

        return onAttemptsExhausted(maxAttempts);
    }
}
