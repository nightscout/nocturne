using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Nocturne.API.Services.BackgroundServices;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

public class ConnectorSyncBudgetTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsABudgetNoSyncCouldFit(int slots)
    {
        var construct = () => new ConnectorSyncBudget(slots);

        construct.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task AcquireAsync_HoldsAtTheBudget_UntilALeaseIsReturned()
    {
        var budget = new ConnectorSyncBudget(slots: 1);

        var held = await budget.AcquireAsync(CancellationToken.None);
        var waiting = budget.AcquireAsync(CancellationToken.None).AsTask();

        (await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(200))))
            .Should().NotBe(waiting, "the budget is exhausted while the first lease is held");
        budget.InFlight.Should().Be(1);

        held.Dispose();

        (await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5))))
            .Should().Be(waiting, "returning the lease hands the slot to the waiter");
        budget.InFlight.Should().Be(1);

        (await waiting).Dispose();
        budget.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task Lease_DisposedTwice_ReturnsTheSlotOnce()
    {
        var budget = new ConnectorSyncBudget(slots: 1);

        var lease = await budget.AcquireAsync(CancellationToken.None);
        lease.Dispose();
        lease.Dispose();

        budget.InFlight.Should().Be(0, "a second dispose must not over-release the semaphore");
    }

    [Fact]
    public async Task AcquireAsync_CancelledWhileWaiting_LeavesTheHeldSlotAlone()
    {
        var budget = new ConnectorSyncBudget(slots: 1);
        using var cts = new CancellationTokenSource();

        using var held = await budget.AcquireAsync(CancellationToken.None);
        var waiting = budget.AcquireAsync(cts.Token).AsTask();

        cts.Cancel();

        await waiting.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        budget.InFlight.Should().Be(1);
    }

    [Fact]
    public void FromConfiguration_WithNothingSet_RunsOnTheDefault()
    {
        var budget = ConnectorSyncBudget.FromConfiguration(new ConfigurationBuilder().Build());

        budget.Slots.Should().Be(ConnectorSyncBudget.DefaultSlots);
    }

    [Theory]
    [InlineData("Connectors:Settings:SyncSlots")]
    [InlineData("Parameters:Connectors:Settings:SyncSlots")]
    public void FromConfiguration_ReadsTheSharedConnectorSettings(string key)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(key, "6")])
            .Build();

        var budget = ConnectorSyncBudget.FromConfiguration(configuration);

        budget.Slots.Should().Be(6);
    }
}
