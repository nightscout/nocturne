using FluentAssertions;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
///     The production backoff curve: 30 seconds doubled per attempt, capped so no single delay
///     outlasts the background sync's per-tenant timeout.
/// </summary>
public class ProductionRetryDelayStrategyTests
{
    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 120)]
    [InlineData(4, 120)]
    public void DelayFor_DoublesFromThirtySecondsAndCapsAtTwoMinutes(int attempt, int seconds)
    {
        ProductionRetryDelayStrategy.DelayFor(attempt).Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public async Task ApplyRetryDelayAsync_CancelledWhileAsleep_EndsWithCancellation()
    {
        var strategy = new ProductionRetryDelayStrategy();
        using var cts = new CancellationTokenSource();

        var delay = strategy.ApplyRetryDelayAsync(0, cts.Token);
        cts.Cancel();

        await FluentActions.Awaiting(() => delay).Should().ThrowAsync<OperationCanceledException>();
    }
}
