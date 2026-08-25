using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
///     Records the attempt each applied delay followed, so a retry test can pin how many delays a
///     run applied and where they fell rather than only that the run finished.
/// </summary>
internal sealed class RecordingRetryDelayStrategy : IRetryDelayStrategy
{
    private readonly List<int> _delayedAttempts = [];

    public IReadOnlyList<int> DelayedAttempts => _delayedAttempts;

    public Task ApplyRetryDelayAsync(int attemptNumber)
    {
        _delayedAttempts.Add(attemptNumber);
        return Task.CompletedTask;
    }
}
