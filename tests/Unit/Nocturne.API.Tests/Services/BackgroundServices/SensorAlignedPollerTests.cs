using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

/// <summary>
/// The scheduler's alignment as a connector declares it on its registration: the cadence decides
/// whether the poller aligns at all and how fast it ticks, and the data source is where it looks for
/// the newest reading.
/// </summary>
public class SensorAlignedPollerTests
{
    private const string Source = "sensor-test-source";
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(300);

    [ConnectorRegistration("SensorTest", "sensor-test", "SENSORTEST", "SensorTest", Source,
        SensorReadingIntervalSeconds = 300)]
    private sealed class SensorConfig : BaseConnectorConfiguration
    {
        protected override void ValidateSourceSpecificConfiguration() { }
    }

    [ConnectorRegistration("PlainTest", "plain-test", "PLAINTEST", "PlainTest", "plain-test-source")]
    private sealed class PlainConfig : BaseConnectorConfiguration
    {
        protected override void ValidateSourceSpecificConfiguration() { }
    }

    [ConnectorRegistration("SourcelessTest", "sourceless-test", "SOURCELESSTEST", "SourcelessTest",
        SensorReadingIntervalSeconds = 300)]
    private sealed class SourcelessConfig : BaseConnectorConfiguration
    {
        protected override void ValidateSourceSpecificConfiguration() { }
    }

    private sealed class ExposedPoller<TConfig>()
        : ConnectorBackgroundService<TConfig>(
            new ServiceCollection().BuildServiceProvider(),
            new ConnectorSyncBudget(),
            ActiveTenantSnapshotTestDoubles.Unread(),
            NullLogger.Instance)
        where TConfig : BaseConnectorConfiguration, new()
    {
        public TimeSpan Tick => PollInterval;

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
        using var poller = new ExposedPoller<SensorConfig>();

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
        using var poller = new ExposedPoller<SensorConfig>();

        var next = await poller.AlignAsync(ScopeWith(PublisherReturning(reading)), now);

        var onTime = DateTime.SpecifyKind(reading, DateTimeKind.Utc) + Cadence + SensorSyncAlignment.PublishBuffer;
        next!.Value.Should().BeOnOrAfter(onTime).And.BeOnOrBefore(onTime + SensorSyncAlignment.JitterFor(Cadence));
    }

    [Fact]
    public async Task WithNoReadingsStored_TheIntervalStaysInCharge()
    {
        using var poller = new ExposedPoller<SensorConfig>();

        var next = await poller.AlignAsync(ScopeWith(PublisherReturning(null)), DateTime.UtcNow);

        next.Should().BeNull();
    }

    [Fact]
    public async Task WithNoGlucosePublisherInScope_TheIntervalStaysInCharge()
    {
        using var poller = new ExposedPoller<SensorConfig>();

        var next = await poller.AlignAsync(new ServiceCollection().BuildServiceProvider(), DateTime.UtcNow);

        next.Should().BeNull();
    }

    /// <summary>A connector with no declared cadence is never aligned and never costs a lookup.</summary>
    [Fact]
    public async Task WithoutADeclaredCadence_NothingIsLookedUp()
    {
        var publisher = new Mock<IGlucosePublisher>(MockBehavior.Strict);
        using var poller = new ExposedPoller<PlainConfig>();

        var next = await poller.AlignAsync(ScopeWith(publisher), DateTime.UtcNow);

        next.Should().BeNull();
        publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ACadenceWithoutADataSource_IsNotAligned()
    {
        var publisher = new Mock<IGlucosePublisher>(MockBehavior.Strict);
        using var poller = new ExposedPoller<SourcelessConfig>();

        var next = await poller.AlignAsync(ScopeWith(publisher), DateTime.UtcNow);

        next.Should().BeNull();
        publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public void AnAlignedConnector_TicksFastEnoughToMeetItsSchedule_AndOthersKeepTheMinute()
    {
        using var aligned = new ExposedPoller<SensorConfig>();
        using var plain = new ExposedPoller<PlainConfig>();
        using var sourceless = new ExposedPoller<SourcelessConfig>();

        aligned.Tick.Should().Be(TimeSpan.FromSeconds(15));
        plain.Tick.Should().Be(TimeSpan.FromMinutes(1));
        sourceless.Tick.Should().Be(TimeSpan.FromMinutes(1));
    }
}
