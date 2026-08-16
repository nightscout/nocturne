using FluentAssertions;
using Nocturne.API.Services.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

[Trait("Category", "Unit")]
public class AlertRuleEvaluationGateTests
{
    [Fact]
    public async Task Acquire_excludes_a_second_holder_of_the_same_rule()
    {
        var gate = new AlertRuleEvaluationGate();
        var ruleId = Guid.NewGuid();

        var held = await gate.AcquireAsync(ruleId, CancellationToken.None);
        var contender = gate.AcquireAsync(ruleId, CancellationToken.None);

        contender.IsCompleted.Should().BeFalse();

        held.Dispose();
        (await contender.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Acquire_does_not_exclude_a_different_rule()
    {
        var gate = new AlertRuleEvaluationGate();

        using var held = await gate.AcquireAsync(Guid.NewGuid(), CancellationToken.None);
        using var other = await gate.AcquireAsync(Guid.NewGuid(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stripes_are_dropped_once_nobody_holds_or_awaits_them()
    {
        var gate = new AlertRuleEvaluationGate();
        var ruleId = Guid.NewGuid();

        var held = await gate.AcquireAsync(ruleId, CancellationToken.None);
        var contender = gate.AcquireAsync(ruleId, CancellationToken.None);
        gate.StripeCount.Should().Be(1);

        held.Dispose();
        (await contender.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();

        gate.StripeCount.Should().Be(0);
    }

    [Fact]
    public async Task A_cancelled_waiter_releases_its_stripe()
    {
        var gate = new AlertRuleEvaluationGate();
        var ruleId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        var held = await gate.AcquireAsync(ruleId, CancellationToken.None);
        var contender = gate.AcquireAsync(ruleId, cts.Token);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => contender).Should().ThrowAsync<OperationCanceledException>();

        held.Dispose();
        gate.StripeCount.Should().Be(0);
    }
}
