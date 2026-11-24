using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Tests.Shared.Connectors;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>Several resume stamps set together go to the store in one write, and the run reads them back.</summary>
public class ConnectorResumeStampsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Earlier = Now.UtcDateTime.AddDays(-3);

    public class TestConfig : BaseConnectorConfiguration;

    private sealed class ProbeConnector(IConnectorPublisher publisher)
        : BaseConnectorService<TestConfig>(
            new HttpClient(),
            new ConnectorServerResolver<TestConfig>(null, null, null),
            NullLogger<ProbeConnector>.Instance,
            new RecordingRetryDelayStrategy(),
            publisher,
            new FakeTimeProvider(Now))
    {
        public (DateTime? Low, DateTime? High, DateTime? Kept) SeenAfterTheWrite { get; private set; }

        protected override string ConnectorSource => "probe-connector";
        public override string ServiceName => "Probe";
        public override List<SyncDataType> SupportedDataTypes => [SyncDataType.Glucose];

        protected override Task<bool> EnsureAuthenticatedAsync(TestConfig config, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        protected override async Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request, TestConfig config, CancellationToken cancellationToken)
        {
            await ResumeStampAsync("low", cancellationToken);
            await SetResumeStampsAsync(new Dictionary<string, DateTime?> { ["low"] = Earlier, ["high"] = null }, cancellationToken);
            SeenAfterTheWrite = (
                await ResumeStampAsync("low", cancellationToken),
                await ResumeStampAsync("high", cancellationToken),
                await ResumeStampAsync("kept", cancellationToken));
            return new SyncResult { Success = true };
        }
    }

    [Fact]
    public async Task Stamps_set_together_are_one_write_that_sets_removes_and_leaves_the_rest()
    {
        var writes = 0;
        var store = new InMemoryConnectorStateStore { BeforeWrite = _ => writes++ };
        store.Resume["high"] = Now.UtcDateTime;
        store.Resume["kept"] = Now.UtcDateTime;
        var probe = new ProbeConnector(new StoringConnectorPublisher(store));

        var result = await probe.SyncDataAsync(new SyncRequest(), new TestConfig(), CancellationToken.None);

        result.Success.Should().BeTrue();
        writes.Should().Be(1);
        store.Resume.Should().BeEquivalentTo(new Dictionary<string, DateTime> { ["low"] = Earlier, ["kept"] = Now.UtcDateTime });
        probe.SeenAfterTheWrite.Should().Be(((DateTime?)Earlier, (DateTime?)null, (DateTime?)Now.UtcDateTime));
    }
}
