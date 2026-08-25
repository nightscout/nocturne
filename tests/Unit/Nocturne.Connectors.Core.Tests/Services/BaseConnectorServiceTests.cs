using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Contracts.V4;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

public class BaseConnectorServiceTests
{
    public class TestConnectorService : BaseConnectorService<TestConfig>
    {
        public TestConnectorService(
            HttpClient httpClient,
            ILogger<TestConnectorService> logger,
            IConnectorPublisher? publisher = null)
            : base(httpClient,
                new ConnectorServerResolver<TestConfig>(null, null, null),
                logger, publisher)
        {
        }

        protected override string ConnectorSource => "test";
        public override string ServiceName => "Test";

        public override Task<bool> AuthenticateAsync() => Task.FromResult(true);

        // These tests exercise the base helpers directly, never a sync run.
        protected override Task<SyncResult> PerformSyncInternalAsync(
            SyncRequest request,
            TestConfig config,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        // Exposes the protected retry helper so its attempt-count behaviour can be tested directly.
        public Task<string?> InvokeExecuteWithRetryAsync(
            Func<Task<string?>> operation,
            IRetryDelayStrategy retryDelayStrategy,
            int maxRetries,
            Func<Task<bool>>? reAuthenticateOnUnauthorized = null)
            => ExecuteWithRetryAsync(
                operation, retryDelayStrategy, reAuthenticateOnUnauthorized, maxRetries);

        // Expose the protected per-run publish-origin resolvers so the watermark→origin mapping
        // and the per-run memoization (anti-flood guard) can be asserted directly.
        public Task<WriteOrigin> CallGlucoseOrigin() => GlucosePublishOriginAsync();
        public Task<WriteOrigin> CallTreatmentOrigin() => TreatmentPublishOriginAsync();
        public Task<WriteOrigin> CallDeviceOrigin() => DevicePublishOriginAsync();
    }

    public class TestConfig : BaseConnectorConfiguration
    {
        protected override void ValidateSourceSpecificConfiguration() { }
    }

    [Fact]
    public void Constructor_WithHttpClient_ShouldNotOwnHttpClient()
    {
        // Arrange
        var httpClient = new HttpClient();
        var logger = Mock.Of<ILogger<TestConnectorService>>();

        // Act
        var service = new TestConnectorService(httpClient, logger);

        // Assert - HttpClient should not be disposed when service is disposed
        service.Dispose();

        // This will throw if HttpClient was disposed
        _ = httpClient.BaseAddress;
    }

    [Fact]
    public void Constructor_WithNullHttpClient_ShouldThrowArgumentNullException()
    {
        // Arrange
        var logger = Mock.Of<ILogger<TestConnectorService>>();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new TestConnectorService(null!, logger));
    }

    [Fact]
    public void Constructor_WithNullLogger_ShouldThrowArgumentNullException()
    {
        // Arrange
        var httpClient = new HttpClient();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new TestConnectorService(httpClient, null!));
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_RetryableError_AttemptsUpToMaxRetries()
    {
        // Arrange: a connector configured to attempt 5 times, hitting a retryable 503 every time
        var service = new TestConnectorService(new HttpClient(), Mock.Of<ILogger<TestConnectorService>>());
        var attempts = 0;
        Func<Task<string?>> alwaysFails = () =>
        {
            attempts++;
            throw new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable);
        };

        var delays = new RecordingRetryDelayStrategy();

        // Act: the helper exhausts every attempt then surfaces the last error
        var act = async () =>
            await service.InvokeExecuteWithRetryAsync(alwaysFails, delays, maxRetries: 5);

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        attempts.Should().Be(5, "maxRetries should drive the number of attempts");
        delays.DelayedAttempts.Should().Equal([0, 1, 2, 3], "five attempts leave four gaps to delay in");
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_ZeroMaxRetries_AttemptsExactlyOnce()
    {
        // Arrange: MaxRetryAttempts of 0 must still try once (clamped to a floor of 1),
        // not skip the operation entirely
        var service = new TestConnectorService(new HttpClient(), Mock.Of<ILogger<TestConnectorService>>());
        var attempts = 0;
        Func<Task<string?>> alwaysFails = () =>
        {
            attempts++;
            throw new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable);
        };

        var delays = new RecordingRetryDelayStrategy();

        // Act
        var act = async () =>
            await service.InvokeExecuteWithRetryAsync(alwaysFails, delays, maxRetries: 0);

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        attempts.Should().Be(1, "0 is clamped to a single attempt");
        delays.DelayedAttempts.Should().BeEmpty("a single attempt has nothing to wait between");
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_UnauthorizedWithSuccessfulReauth_RetriesImmediatelyWithoutDelay()
    {
        // Arrange: every attempt 401s and every re-authentication succeeds, so the run only ends
        // when the attempt budget does
        var service = new TestConnectorService(new HttpClient(), Mock.Of<ILogger<TestConnectorService>>());
        var delays = new RecordingRetryDelayStrategy();
        var attempts = 0;
        var reAuthentications = 0;

        Func<Task<string?>> alwaysUnauthorized = () =>
        {
            attempts++;
            throw new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);
        };

        // Act
        var result = await service.InvokeExecuteWithRetryAsync(
            alwaysUnauthorized,
            delays,
            maxRetries: 3,
            () =>
            {
                reAuthentications++;
                return Task.FromResult(true);
            });

        // Assert
        result.Should().BeNull();
        attempts.Should().Be(3, "a re-authenticated retry spends an attempt, so the run terminates");
        reAuthentications.Should().Be(3);
        delays.DelayedAttempts.Should().BeEmpty(
            "fresh credentials replace the backoff rather than waiting one out");
        service.FailedRequestCount.Should().Be(1, "the exhausted run tracks exactly one failure");
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_UnauthorizedWithFailedReauth_StopsWithoutSpendingAnotherAttempt()
    {
        // Arrange: re-authentication fails, so retrying the same rejected credentials is pointless
        var service = new TestConnectorService(new HttpClient(), Mock.Of<ILogger<TestConnectorService>>());
        var delays = new RecordingRetryDelayStrategy();
        var attempts = 0;

        Func<Task<string?>> alwaysUnauthorized = () =>
        {
            attempts++;
            throw new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);
        };

        // Act
        var result = await service.InvokeExecuteWithRetryAsync(
            alwaysUnauthorized, delays, maxRetries: 3, () => Task.FromResult(false));

        // Assert
        result.Should().BeNull();
        attempts.Should().Be(1, "there is nothing to retry with once re-authentication fails");
        delays.DelayedAttempts.Should().BeEmpty();
        service.FailedRequestCount.Should().Be(1);
    }

    private static (TestConnectorService service, Mock<IConnectorPublisher> publisher,
        Mock<IGlucosePublisher> glucose, Mock<ITreatmentPublisher> treatments,
        Mock<IDevicePublisher> device) BuildServiceWithPublisher(
        bool isAvailable = true)
    {
        var glucose = new Mock<IGlucosePublisher>();
        var treatments = new Mock<ITreatmentPublisher>();
        var device = new Mock<IDevicePublisher>();
        var publisher = new Mock<IConnectorPublisher>();
        publisher.SetupGet(p => p.IsAvailable).Returns(isAvailable);
        publisher.SetupGet(p => p.Glucose).Returns(glucose.Object);
        publisher.SetupGet(p => p.Treatments).Returns(treatments.Object);
        publisher.SetupGet(p => p.Device).Returns(device.Object);

        var service = new TestConnectorService(
            new HttpClient(), Mock.Of<ILogger<TestConnectorService>>(), publisher.Object);
        return (service, publisher, glucose, treatments, device);
    }

    [Fact]
    public async Task GlucosePublishOriginAsync_NoPriorData_ReturnsBackfill()
    {
        // Arrange: null watermark = no prior glucose for this source = first-ever sync
        var (service, _, glucose, _, _) = BuildServiceWithPublisher();
        glucose
            .Setup(g => g.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)null);

        // Act
        var origin = await service.CallGlucoseOrigin();

        // Assert
        origin.Should().Be(WriteOrigin.Backfill);
    }

    [Fact]
    public async Task GlucosePublishOriginAsync_PriorDataExists_ReturnsLive()
    {
        // Arrange: a non-null watermark means the source already has glucose, so this is a live catch-up
        var (service, _, glucose, _, _) = BuildServiceWithPublisher();
        glucose
            .Setup(g => g.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTime.UtcNow);

        // Act
        var origin = await service.CallGlucoseOrigin();

        // Assert
        origin.Should().Be(WriteOrigin.Live);
    }

    [Fact]
    public async Task GlucosePublishOriginAsync_CalledTwice_MemoizesAndQueriesWatermarkOnce()
    {
        // Arrange
        var (service, _, glucose, _, _) = BuildServiceWithPublisher();
        glucose
            .Setup(g => g.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)null);

        // Act: two calls within the same run
        var first = await service.CallGlucoseOrigin();
        var second = await service.CallGlucoseOrigin();

        // Assert: identical result, and the watermark was queried only once (the anti-flood memo)
        first.Should().Be(WriteOrigin.Backfill);
        second.Should().Be(first);
        glucose.Verify(
            g => g.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GlucosePublishOriginAsync_PublisherUnavailable_ReturnsLiveWithoutQuerying()
    {
        // Arrange: an unavailable publisher can't publish anyway, so origin defaults to Live and skips the query
        var (service, _, glucose, _, _) = BuildServiceWithPublisher(isAvailable: false);

        // Act
        var origin = await service.CallGlucoseOrigin();

        // Assert
        origin.Should().Be(WriteOrigin.Live);
        glucose.Verify(
            g => g.GetLatestEntryTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TreatmentPublishOriginAsync_NoPriorData_ReturnsBackfill()
    {
        // Arrange
        var (service, _, _, treatments, _) = BuildServiceWithPublisher();
        treatments
            .Setup(t => t.GetLatestTreatmentTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)null);

        // Act
        var origin = await service.CallTreatmentOrigin();

        // Assert
        origin.Should().Be(WriteOrigin.Backfill);
    }

    [Fact]
    public async Task TreatmentPublishOriginAsync_PriorDataExists_ReturnsLive()
    {
        // Arrange
        var (service, _, _, treatments, _) = BuildServiceWithPublisher();
        treatments
            .Setup(t => t.GetLatestTreatmentTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTime.UtcNow);

        // Act
        var origin = await service.CallTreatmentOrigin();

        // Assert
        origin.Should().Be(WriteOrigin.Live);
    }

    [Fact]
    public async Task TreatmentPublishOriginAsync_CalledTwice_MemoizesAndQueriesWatermarkOnce()
    {
        // Arrange
        var (service, _, _, treatments, _) = BuildServiceWithPublisher();
        treatments
            .Setup(t => t.GetLatestTreatmentTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)null);

        // Act
        var first = await service.CallTreatmentOrigin();
        var second = await service.CallTreatmentOrigin();

        // Assert: identical result, and the watermark was queried only once (the anti-flood memo)
        first.Should().Be(WriteOrigin.Backfill);
        second.Should().Be(first);
        treatments.Verify(
            t => t.GetLatestTreatmentTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DevicePublishOriginAsync_NoPriorData_ReturnsBackfill()
    {
        // Arrange: null watermark = no prior device status for this source = first-ever sync
        var (service, _, _, _, device) = BuildServiceWithPublisher();
        device
            .Setup(d => d.GetLatestDeviceStatusTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)null);

        // Act
        var origin = await service.CallDeviceOrigin();

        // Assert
        origin.Should().Be(WriteOrigin.Backfill);
    }

    [Fact]
    public async Task DevicePublishOriginAsync_PriorDataExists_ReturnsLive()
    {
        // Arrange: a non-null watermark means the source already has device status, so this is a live catch-up
        var (service, _, _, _, device) = BuildServiceWithPublisher();
        device
            .Setup(d => d.GetLatestDeviceStatusTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTime.UtcNow);

        // Act
        var origin = await service.CallDeviceOrigin();

        // Assert
        origin.Should().Be(WriteOrigin.Live);
    }

    [Fact]
    public async Task DevicePublishOriginAsync_CalledTwice_MemoizesAndQueriesWatermarkOnce()
    {
        // Arrange
        var (service, _, _, _, device) = BuildServiceWithPublisher();
        device
            .Setup(d => d.GetLatestDeviceStatusTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)null);

        // Act
        var first = await service.CallDeviceOrigin();
        var second = await service.CallDeviceOrigin();

        // Assert: identical result, and the watermark was queried only once (the anti-flood memo)
        first.Should().Be(WriteOrigin.Backfill);
        second.Should().Be(first);
        device.Verify(
            d => d.GetLatestDeviceStatusTimestampAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
