using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Models;
using Nocturne.Tools.Connect.Configuration;
using Nocturne.Tools.Connect.Services;
using Xunit;

namespace Nocturne.Tools.Connect.Tests.Services;

public class ConnectorExecutionServiceDryRunTests
{
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(3);

    private static ConnectConfiguration DexcomConfig() =>
        new()
        {
            NightscoutUrl = "https://example.com",
            NightscoutApiSecret = "supersecretvalue",
            ConnectSource = "dexcom",
            DexcomUsername = "user",
            DexcomPassword = "pass",
        };

    private static Mock<IConnectorService<IConnectorConfiguration>> AuthenticatedConnector(
        Action? onFetch = null
    )
    {
        var connector = new Mock<IConnectorService<IConnectorConfiguration>>();
        connector.Setup(c => c.AuthenticateAsync()).ReturnsAsync(true);
        connector
            .Setup(c => c.FetchGlucoseDataAsync(It.IsAny<DateTime?>()))
            .ReturnsAsync(new Entry[] { new(), new() })
            .Callback(() => onFetch?.Invoke());
        return connector;
    }

    [Fact]
    public async Task RunOnceDryRun_FetchesOnlyTheDefaultSyncWindow()
    {
        var connector = AuthenticatedConnector();
        var service = new StubbedConnectorExecutionService(connector.Object);
        var before = DateTime.UtcNow;

        var result = await service.ExecuteConnectorAsync(
            DexcomConfig(),
            once: true,
            dryRun: true
        );

        result.Should().BeTrue();
        AssertWithinDefaultWindow(CapturedSince(connector), before);
    }

    [Fact]
    public async Task RunOnceDryRun_HonoursAnExplicitSince()
    {
        var connector = AuthenticatedConnector();
        var service = new StubbedConnectorExecutionService(connector.Object);
        var since = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        await service.ExecuteConnectorAsync(DexcomConfig(), once: true, dryRun: true, since: since);

        CapturedSince(connector).Should().Be(since);
    }

    [Fact]
    public async Task DaemonDryRun_FetchesOnlyTheDefaultSyncWindow()
    {
        using var cts = new CancellationTokenSource();
        // The daemon loop exits only on cancellation, so end it once the first cycle has fetched.
        var connector = AuthenticatedConnector(cts.Cancel);
        var service = new StubbedConnectorExecutionService(connector.Object);
        var before = DateTime.UtcNow;

        await service.ExecuteConnectorAsync(
            DexcomConfig(),
            daemon: true,
            dryRun: true,
            cancellationToken: cts.Token
        );

        AssertWithinDefaultWindow(CapturedSince(connector), before);
    }

    private static void AssertWithinDefaultWindow(DateTime? since, DateTime before)
    {
        since.Should().NotBeNull();
        since!.Value.Should().BeOnOrAfter(before - DefaultWindow);
        since.Value.Should().BeOnOrBefore(DateTime.UtcNow - DefaultWindow);
    }

    private static DateTime? CapturedSince(
        Mock<IConnectorService<IConnectorConfiguration>> connector
    )
    {
        var fetches = connector
            .Invocations.Where(i => i.Method.Name == "FetchGlucoseDataAsync")
            .ToList();
        fetches.Should().ContainSingle();
        return (DateTime?)fetches[0].Arguments[0];
    }

    private sealed class StubbedConnectorExecutionService(
        IConnectorService<IConnectorConfiguration> connector
    )
        : ConnectorExecutionService(
            NullLogger<ConnectorExecutionService>.Instance,
            NullLoggerFactory.Instance
        )
    {
        protected override IConnectorService<IConnectorConfiguration>? CreateConnectorService(
            IConnectorConfiguration config
        ) => connector;
    }
}
