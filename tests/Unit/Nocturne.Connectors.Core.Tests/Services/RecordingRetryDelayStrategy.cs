using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
///     Records the attempt each applied delay followed, so a retry test can pin how many delays a
///     run applied and where they fell rather than only that the run finished.
/// </summary>
internal sealed class RecordingRetryDelayStrategy : IRetryDelayStrategy
{
    private readonly List<int> _delayedAttempts = [];
    private readonly List<CancellationToken> _tokens = [];

    public IReadOnlyList<int> DelayedAttempts => _delayedAttempts;

    public IReadOnlyList<CancellationToken> Tokens => _tokens;

    public Task ApplyRetryDelayAsync(int attemptNumber, CancellationToken cancellationToken)
    {
        _delayedAttempts.Add(attemptNumber);
        _tokens.Add(cancellationToken);
        return Task.CompletedTask;
    }
}
