using FluentAssertions;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
///     The loop every connector retry path runs on: where the delays fall, and which decision buys
///     an attempt without one.
/// </summary>
public class ConnectorRetryLoopTests
{
    [Fact]
    public async Task RunAsync_RetryAfterDelay_DelaysBetweenAttemptsButNotAfterTheLast()
    {
        var delays = new RecordingRetryDelayStrategy();
        var announced = new List<int>();
        var spent = new List<int>();

        var result = await ConnectorRetryLoop.RunAsync<string>(
            (attempt, _) =>
            {
                spent.Add(attempt);
                return Task.FromResult(RetryStep<string>.RetryAfterDelay);
            },
            delays,
            maxAttempts: 3,
            _ => null,
            CancellationToken.None,
            announced.Add);

        result.Should().BeNull();
        spent.Should().Equal(0, 1, 2);
        delays.DelayedAttempts.Should().Equal([0, 1], "three attempts leave two gaps to delay in");
        announced.Should().Equal([0, 1], "the hook announces a delay, so it cannot fire after the last attempt");
    }

    [Fact]
    public async Task RunAsync_RetryImmediately_SpendsAnAttemptWithoutDelaying()
    {
        var delays = new RecordingRetryDelayStrategy();
        var announced = new List<int>();
        var spent = new List<int>();

        var result = await ConnectorRetryLoop.RunAsync<string>(
            (attempt, _) =>
            {
                spent.Add(attempt);
                return Task.FromResult(RetryStep<string>.RetryImmediately);
            },
            delays,
            maxAttempts: 3,
            _ => null,
            CancellationToken.None,
            announced.Add);

        result.Should().BeNull();
        spent.Should().Equal([0, 1, 2], "an immediate retry still consumes an attempt, so the run terminates");
        delays.DelayedAttempts.Should().BeEmpty();
        announced.Should().BeEmpty();
    }
}
