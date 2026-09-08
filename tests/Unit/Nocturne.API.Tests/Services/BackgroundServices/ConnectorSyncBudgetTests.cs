using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Connectors.Core.Extensions;
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
    public void NextStartupOffset_DividesThePollIntervalAmongThePollers_InStartOrder()
    {
        var budget = new ConnectorSyncBudget(pollerCount: 4);
        var interval = TimeSpan.FromSeconds(60);

        budget.NextStartupOffset(interval).Should().Be(TimeSpan.Zero);
        budget.NextStartupOffset(interval).Should().Be(TimeSpan.FromSeconds(15));
        budget.NextStartupOffset(interval).Should().Be(TimeSpan.FromSeconds(30));
        budget.NextStartupOffset(interval).Should().Be(TimeSpan.FromSeconds(45));
        budget.NextStartupOffset(interval).Should().Be(TimeSpan.Zero, "a poller beyond the count wraps");
    }

    [Fact]
    public void FromConfiguration_WithNothingSet_RunsOnTheDefault()
    {
        var budget = ConnectorSyncBudget.FromConfiguration(
            new ConfigurationBuilder().Build(), new ServiceCollection());

        budget.Slots.Should().Be(ConnectorSyncBudget.DefaultSlots);
        budget.PollerCount.Should().Be(1, "a host with no pollers still needs a valid budget");
    }

    [Theory]
    [InlineData("Connectors:Settings:SyncSlots")]
    [InlineData("Parameters:Connectors:Settings:SyncSlots")]
    public void FromConfiguration_ReadsTheSharedConnectorSettings(string key)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(key, "6")])
            .Build();

        var budget = ConnectorSyncBudget.FromConfiguration(configuration, new ServiceCollection());

        budget.Slots.Should().Be(6);
    }

    /// <summary>
    /// The phase count has to be the pollers actually scheduled, so a connector that is added or
    /// disabled changes the stagger without anyone editing a constant.
    /// </summary>
    [Fact]
    public void FromConfiguration_CountsTheRegisteredPollers()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddConnectors(configuration, typeof(ConnectorBackgroundService<,>));
        services.AddHostedService<UnrelatedHostedService>();

        var budget = ConnectorSyncBudget.FromConfiguration(configuration, services);

        var pollers = services.Count(d =>
            d.ServiceType == typeof(IHostedService) && d.ImplementationType != typeof(UnrelatedHostedService));
        pollers.Should().BeGreaterThan(1);
        budget.PollerCount.Should().Be(pollers);
    }

    private sealed class UnrelatedHostedService : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }
}
