using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Jobs;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

/// <summary>
/// The scheduler's alignment as a connector declares it on its registration: the cadence decides
/// whether the poller aligns at all, and the data source is where it looks for the newest reading.
/// </summary>
public class SensorAlignedPollerTests
{
    private const string Source = "sensor-test-source";
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(300);

    [ConnectorRegistration("SensorTest", "sensor-test", "SENSORTEST", "SensorTest", Source,
        SensorReadingIntervalSeconds = 300)]
    private sealed class SensorConfig : BaseConnectorConfiguration;

    [ConnectorRegistration("PlainTest", "plain-test", "PLAINTEST", "PlainTest", "plain-test-source")]
    private sealed class PlainConfig : BaseConnectorConfiguration;

    [ConnectorRegistration("SourcelessTest", "sourceless-test", "SOURCELESSTEST", "SourcelessTest",
        SensorReadingIntervalSeconds = 300)]
    private sealed class SourcelessConfig : BaseConnectorConfiguration;

    private sealed class ExposedPoller<TConfig>()
        : ConnectorBackgroundService<TConfig>(
            new ConnectorSyncBudget(),
            Mock.Of<IJobLedger>(),
            NullLogger.Instance,
            TimeProvider.System)
        where TConfig : BaseConnectorConfiguration, new()
    {
        public Task<DateTime?> AlignAsync(IServiceProvider scope, DateTime now) =>
            GetAlignedSyncTimeAsync(scope, new TConfig(), now, CancellationToken.None);

        protected override Task<SyncResult> PerformSyncAsync(
            IServiceProvider scopeProvider,
            TConfig config,
            CancellationToken cancellationToken,
            ISyncProgressReporter? progressReporter = null) =>
            Task.FromResult(new SyncResult { Success = true });
    }

    private static IServiceProvider ScopeWith(Mock<IGlucosePublisher> publisher) =>
        new ServiceCollection().AddSingleton(publisher.Object).BuildServiceProvider();

    private static Mock<IGlucosePublisher> PublisherReturning(DateTime? latest)
    {
        var publisher = new Mock<IGlucosePublisher>(MockBehavior.Strict);
        publisher
            .Setup(p => p.GetLatestSensorGlucoseTimestampAsync(Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(latest);
        return publisher;
    }

    [Fact]
    public async Task ADeclaredCadence_AlignsToTheNewestReadingUnderTheConnectorsDataSource()
    {
        var now = DateTime.UtcNow;
        var reading = now.AddMinutes(-2);
        var publisher = PublisherReturning(reading);
        var poller = new ExposedPoller<SensorConfig>();

        var next = await poller.AlignAsync(ScopeWith(publisher), now);

        var onTime = reading + Cadence + SensorSyncAlignment.PublishBuffer;
        next.Should().NotBeNull();
        next!.Value.Should().BeOnOrAfter(onTime).And.BeOnOrBefore(onTime + SensorSyncAlignment.JitterFor(Cadence));
        publisher.Verify(p => p.GetLatestSensorGlucoseTimestampAsync(Source, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A timestamp read back without a kind is the UTC it was stored as, not local time.</summary>
    [Fact]
    public async Task AReadingWithoutAKind_IsTakenAsUtc()
    {
        var now = DateTime.UtcNow;
        var reading = DateTime.SpecifyKind(now.AddMinutes(-2), DateTimeKind.Unspecified);
        var poller = new ExposedPoller<SensorConfig>();

        var next = await poller.AlignAsync(ScopeWith(PublisherReturning(reading)), now);

        var onTime = DateTime.SpecifyKind(reading, DateTimeKind.Utc) + Cadence + SensorSyncAlignment.PublishBuffer;
        next!.Value.Should().BeOnOrAfter(onTime).And.BeOnOrBefore(onTime + SensorSyncAlignment.JitterFor(Cadence));
    }

    [Fact]
    public async Task WithNoReadingsStored_TheIntervalStaysInCharge()
    {
        var poller = new ExposedPoller<SensorConfig>();

        var next = await poller.AlignAsync(ScopeWith(PublisherReturning(null)), DateTime.UtcNow);

        next.Should().BeNull();
    }

    [Fact]
    public async Task WithNoGlucosePublisherInScope_TheIntervalStaysInCharge()
    {
        var poller = new ExposedPoller<SensorConfig>();

        var next = await poller.AlignAsync(new ServiceCollection().BuildServiceProvider(), DateTime.UtcNow);

        next.Should().BeNull();
    }

    /// <summary>A connector with no declared cadence is never aligned and never costs a lookup.</summary>
    [Fact]
    public async Task WithoutADeclaredCadence_NothingIsLookedUp()
    {
        var publisher = new Mock<IGlucosePublisher>(MockBehavior.Strict);
        var poller = new ExposedPoller<PlainConfig>();

        var next = await poller.AlignAsync(ScopeWith(publisher), DateTime.UtcNow);

        next.Should().BeNull();
        publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ACadenceWithoutADataSource_IsNotAligned()
    {
        var publisher = new Mock<IGlucosePublisher>(MockBehavior.Strict);
        var poller = new ExposedPoller<SourcelessConfig>();

        var next = await poller.AlignAsync(ScopeWith(publisher), DateTime.UtcNow);

        next.Should().BeNull();
        publisher.VerifyNoOtherCalls();
    }
}
