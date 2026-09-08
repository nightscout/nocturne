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
    public void Constructor_RejectsABudgetNoSyncCouldFit(int size)
    {
        var construct = () => new ConnectorSyncBudget(size);

        construct.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task AcquireAsync_HoldsAtTheBudget_UntilALeaseIsReturned()
    {
        using var budget = new ConnectorSyncBudget(maxConcurrentTenantSyncs: 1);

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
        using var budget = new ConnectorSyncBudget(maxConcurrentTenantSyncs: 1);

        var lease = await budget.AcquireAsync(CancellationToken.None);
        lease.Dispose();
        lease.Dispose();

        budget.InFlight.Should().Be(0, "a second dispose must not over-release the semaphore");
    }

    [Fact]
    public async Task AcquireAsync_CancelledWhileWaiting_LeavesTheHeldSlotAlone()
    {
        using var budget = new ConnectorSyncBudget(maxConcurrentTenantSyncs: 1);
        using var cts = new CancellationTokenSource();

        using var held = await budget.AcquireAsync(CancellationToken.None);
        var waiting = budget.AcquireAsync(cts.Token).AsTask();

        cts.Cancel();

        await waiting.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        budget.InFlight.Should().Be(1);
    }

    [Fact]
    public void NextStartupOffset_IsOneStaggerMoreForEachPollerAfterTheFirst()
    {
        using var budget = new ConnectorSyncBudget(startupStagger: TimeSpan.FromSeconds(4));

        budget.NextStartupOffset().Should().Be(TimeSpan.Zero);
        budget.NextStartupOffset().Should().Be(TimeSpan.FromSeconds(4));
        budget.NextStartupOffset().Should().Be(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void FromConfiguration_WithNothingSet_RunsOnTheDefault()
    {
        using var budget = ConnectorSyncBudget.FromConfiguration(new ConfigurationBuilder().Build());

        budget.MaxConcurrentTenantSyncs.Should().Be(ConnectorSyncBudget.DefaultMaxConcurrentTenantSyncs);
        budget.StartupStagger.Should().Be(ConnectorSyncBudget.DefaultStartupStagger);
    }

    [Theory]
    [InlineData("Connectors:Settings:MaxConcurrentTenantSyncs")]
    [InlineData("Parameters:Connectors:Settings:MaxConcurrentTenantSyncs")]
    public void FromConfiguration_ReadsTheSharedConnectorSettings(string key)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(key, "6")])
            .Build();

        using var budget = ConnectorSyncBudget.FromConfiguration(configuration);

        budget.MaxConcurrentTenantSyncs.Should().Be(6);
    }
}
