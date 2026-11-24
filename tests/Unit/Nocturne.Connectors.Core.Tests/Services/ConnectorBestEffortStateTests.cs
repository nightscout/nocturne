using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Tests.Shared.Connectors;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
/// A store that is down does not fail a run that can do without its state; a defect in the code
/// that reads or writes it does.
/// </summary>
public class ConnectorBestEffortStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Earlier = Now.UtcDateTime.AddDays(-3);

    private static ConnectorStateUnavailableException Down() =>
        new("connection refused", new InvalidOperationException("connection refused"));

    public class TestConfig : BaseConnectorConfiguration;

    private sealed class RecordingLogger : ILogger<ProbeConnector>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private sealed class ProbeConnector(IConnectorPublisher publisher, RecordingLogger logger)
        : BaseConnectorService<TestConfig>(
            new HttpClient(),
            new ConnectorServerResolver<TestConfig>(null, null, null),
            logger,
            new RecordingRetryDelayStrategy(),
            publisher,
            new FakeTimeProvider(Now))
    {
        public bool CarriedOn { get; private set; }

        protected override string ConnectorSource => "probe-connector";
        public override string ServiceName => "Probe";
        public override List<SyncDataType> SupportedDataTypes => [SyncDataType.Glucose];

        protected override Task<bool> EnsureAuthenticatedAsync(TestConfig config, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        protected override async Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request, TestConfig config, CancellationToken cancellationToken)
        {
            await WriteStamp(cancellationToken);
            CarriedOn = true;
            return new SyncResult { Success = true };
        }

        public Task<DateTime?> ReadStamp(string description, DateTime? fallback, CancellationToken cancellationToken) =>
            ReadStateBestEffortAsync(description, fallback, () => ResumeStampAsync("stamp", cancellationToken), cancellationToken);

        public Task<bool> WriteStamp(CancellationToken cancellationToken) =>
            WriteStateBestEffortAsync(
                "record the stamp", () => SetResumeStampAsync("stamp", Earlier, cancellationToken), cancellationToken);
    }

    private static (ProbeConnector Connector, RecordingLogger Log) Probe(InMemoryConnectorStateStore store)
    {
        var log = new RecordingLogger();
        return (new ProbeConnector(new StoringConnectorPublisher(store), log), log);
    }

    [Fact]
    public async Task A_run_goes_on_when_the_store_is_down_for_a_best_effort_write_and_warns_once()
    {
        var store = new InMemoryConnectorStateStore { BeforeWrite = _ => throw Down() };
        var (probe, log) = Probe(store);

        var result = await probe.SyncDataAsync(new SyncRequest(), new TestConfig(), CancellationToken.None);

        result.Success.Should().BeTrue();
        probe.CarriedOn.Should().BeTrue();
        store.Resume.Should().BeEmpty();
        var warning = log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        warning.Message.Should().Be("[probe-connector] Could not record the stamp");
        warning.Exception.Should().BeOfType<ConnectorStateUnavailableException>();
    }

    [Fact]
    public async Task A_best_effort_write_that_lands_returns_true_and_logs_nothing()
    {
        var store = new InMemoryConnectorStateStore();
        var (probe, log) = Probe(store);

        (await probe.WriteStamp(CancellationToken.None)).Should().BeTrue();

        store.Resume["stamp"].Should().Be(Earlier);
        log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_best_effort_write_that_the_store_refuses_returns_false()
    {
        var (probe, _) = Probe(new InMemoryConnectorStateStore { BeforeWrite = _ => throw Down() });

        (await probe.WriteStamp(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_defect_in_a_best_effort_write_is_not_swallowed()
    {
        var (probe, log) = Probe(new InMemoryConnectorStateStore
        {
            BeforeWrite = _ => throw new InvalidOperationException("a second operation was started on this context"),
        });

        var write = () => probe.WriteStamp(CancellationToken.None);

        await write.Should().ThrowAsync<InvalidOperationException>();
        log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_best_effort_read_returns_its_fallback_when_the_store_is_down()
    {
        var (probe, log) = Probe(new InMemoryConnectorStateStore { BeforeRead = _ => throw Down() });

        var stamp = await probe.ReadStamp("read the stamp; assuming none", null, CancellationToken.None);

        stamp.Should().BeNull();
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Be("[probe-connector] Could not read the stamp; assuming none");
    }

    [Fact]
    public async Task A_best_effort_read_returns_what_the_store_holds_when_it_is_up()
    {
        var store = new InMemoryConnectorStateStore();
        store.Resume["stamp"] = Earlier;
        var (probe, log) = Probe(store);

        (await probe.ReadStamp("read the stamp", null, CancellationToken.None)).Should().Be(Earlier);
        log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_defect_in_a_best_effort_read_is_not_swallowed()
    {
        var (probe, _) = Probe(new InMemoryConnectorStateStore { BeforeRead = _ => throw new InvalidCastException() });

        var read = () => probe.ReadStamp("read the stamp", Earlier, CancellationToken.None);

        await read.Should().ThrowAsync<InvalidCastException>();
    }

    [Fact]
    public async Task A_store_that_times_out_while_the_caller_is_still_live_takes_the_fallback()
    {
        var (probe, _) = Probe(new InMemoryConnectorStateStore
        {
            BeforeRead = _ => throw new TaskCanceledException("The store timed out."),
        });

        (await probe.ReadStamp("read the stamp", Earlier, CancellationToken.None)).Should().Be(Earlier);
    }

    [Fact]
    public async Task A_cancellation_the_caller_asked_for_propagates_from_a_read_and_a_write()
    {
        using var cancellation = new CancellationTokenSource();
        var (probe, log) = Probe(new InMemoryConnectorStateStore
        {
            BeforeRead = token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            },
            BeforeWrite = token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            },
        });

        var read = () => probe.ReadStamp("read the stamp", null, cancellation.Token);
        await read.Should().ThrowAsync<OperationCanceledException>();

        var write = () => probe.WriteStamp(cancellation.Token);
        await write.Should().ThrowAsync<OperationCanceledException>();
        log.Entries.Should().BeEmpty();
    }
}
