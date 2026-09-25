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
    public async Task An_exclusive_section_reenters_its_own_rule_and_excludes_other_flows()
    {
        var gate = new AlertRuleEvaluationGate();
        var ruleId = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var section = gate.RunExclusiveAsync(ruleId, async () =>
        {
            using var inner = await gate.AcquireAsync(ruleId, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            entered.SetResult();
            await release.Task;
            return 1;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var contender = gate.AcquireAsync(ruleId, CancellationToken.None);
        contender.IsCompleted.Should().BeFalse();

        release.SetResult();
        (await section).Should().Be(1);
        (await contender.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        gate.StripeCount.Should().Be(0);
    }

    [Fact]
    public async Task Work_that_outlives_an_exclusive_section_takes_the_lease_again()
    {
        var gate = new AlertRuleEvaluationGate();
        var ruleId = Guid.NewGuid();
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IDisposable>? escaped = null;

        await gate.RunExclusiveAsync(ruleId, () =>
        {
            escaped = Task.Run(async () =>
            {
                await go.Task;
                return await gate.AcquireAsync(ruleId, CancellationToken.None);
            });
            return Task.FromResult(0);
        }, CancellationToken.None);

        var holder = await gate.AcquireAsync(ruleId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        go.SetResult();
        await Task.Delay(100);
        escaped!.IsCompleted.Should().BeFalse("the section's lease was released when it returned");

        holder.Dispose();
        (await escaped.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        gate.StripeCount.Should().Be(0);
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
