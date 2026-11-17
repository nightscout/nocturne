using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Core.Contracts.Jobs;

namespace Nocturne.API.Tests.Services.BackgroundServices;

public class ConnectorRealtimeSupervisorTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Listeners start when the host does, and every supervision interval after that the pass runs
    /// again, which is what replaces a listener that has died.
    /// </summary>
    [Fact]
    public async Task ListenersStartAtOnce_AndAgainEverySupervisionInterval()
    {
        var clock = new FakeTimeProvider();
        var poller = new FakeListenerPoller();
        var sut = Supervisor(clock, poller);

        await sut.StartAsync(default);
        try
        {
            await poller.WaitForStartsAsync(1).WaitAsync(Wait);

            clock.Advance(ConnectorRealtimeSupervisor.SupervisionInterval - TimeSpan.FromSeconds(1));
            await Task.Delay(100);
            poller.Starts.Should().Be(1, "the pass is not repeated inside the interval");

            clock.Advance(TimeSpan.FromSeconds(1));
            await poller.WaitForStartsAsync(2).WaitAsync(Wait);
        }
        finally
        {
            await sut.StopAsync(default);
        }

        poller.Stops.Should().Be(1, "shutdown tears the listeners down");
    }

    [Fact]
    public async Task APollerWhoseListenersFailToStart_DoesNotKeepTheOthersFromStarting()
    {
        var failing = new FakeListenerPoller { Throws = true };
        var healthy = new FakeListenerPoller();

        await Supervisor(new FakeTimeProvider(), failing, healthy).SuperviseAsync(default);

        failing.Starts.Should().Be(1);
        healthy.Starts.Should().Be(1);
    }

    [Fact]
    public async Task WithNoListenerPollers_TheSupervisorEndsAtOnce()
    {
        var sut = new ConnectorRealtimeSupervisor([new PlainJob()], new FakeTimeProvider(), NullLogger<ConnectorRealtimeSupervisor>.Instance);

        await sut.StartAsync(default);

        await sut.ExecuteTask!.WaitAsync(Wait);
        await sut.StopAsync(default);
    }

    private static ConnectorRealtimeSupervisor Supervisor(TimeProvider clock, params FakeListenerPoller[] pollers) =>
        new([new PlainJob(), .. pollers], clock, NullLogger<ConnectorRealtimeSupervisor>.Instance);

    private sealed class PlainJob : IScheduledJob
    {
        public string Name => "plain";

        public JobEnrolment Enrolment => JobEnrolment.PerTenant;

        public Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct) => Task.FromResult(JobOutcome.Done);
    }

    private sealed class FakeListenerPoller : IScheduledJob, IConnectorRealtimeListeners
    {
        private int _starts;
        private int _stops;

        public bool Throws { get; init; }

        public int Starts => Volatile.Read(ref _starts);

        public int Stops => Volatile.Read(ref _stops);

        public string Name => $"listener-{GetHashCode()}";

        public JobEnrolment Enrolment => JobEnrolment.PerTenant;

        public Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct) => Task.FromResult(JobOutcome.Done);

        public Task StartRealtimeListenersAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _starts);
            return Throws ? Task.FromException(new InvalidOperationException("upstream unreachable")) : Task.CompletedTask;
        }

        public Task StopRealtimeListenersAsync()
        {
            Interlocked.Increment(ref _stops);
            return Task.CompletedTask;
        }

        public async Task WaitForStartsAsync(int count)
        {
            while (Starts < count)
                await Task.Delay(10);
        }
    }
}
