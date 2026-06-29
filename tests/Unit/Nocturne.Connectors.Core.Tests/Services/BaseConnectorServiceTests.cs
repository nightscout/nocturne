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
        public override Task<IEnumerable<Nocturne.Core.Models.Entry>> FetchGlucoseDataAsync(DateTime? since = null)
            => Task.FromResult(Enumerable.Empty<Nocturne.Core.Models.Entry>());

        // Exposes the protected retry helper so its attempt-count behaviour can be tested directly.
        public Task<string?> InvokeExecuteWithRetryAsync(
            Func<Task<string?>> operation,
            IRetryDelayStrategy retryDelayStrategy,
            int maxRetries)
            => ExecuteWithRetryAsync(operation, retryDelayStrategy, maxRetries: maxRetries);

        // Expose the protected per-run publish-origin resolvers so the watermark→origin mapping
        // and the per-run memoization (anti-flood guard) can be asserted directly.
        public Task<WriteOrigin> CallGlucoseOrigin() => GlucosePublishOriginAsync();
        public Task<WriteOrigin> CallTreatmentOrigin() => TreatmentPublishOriginAsync();
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

        // Act: the helper exhausts every attempt then surfaces the last error
        var act = async () =>
            await service.InvokeExecuteWithRetryAsync(alwaysFails, Mock.Of<IRetryDelayStrategy>(), maxRetries: 5);

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        attempts.Should().Be(5, "maxRetries should drive the number of attempts");
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

        // Act
        var act = async () =>
            await service.InvokeExecuteWithRetryAsync(alwaysFails, Mock.Of<IRetryDelayStrategy>(), maxRetries: 0);

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        attempts.Should().Be(1, "0 is clamped to a single attempt");
    }

    private static (TestConnectorService service, Mock<IConnectorPublisher> publisher,
        Mock<IGlucosePublisher> glucose, Mock<ITreatmentPublisher> treatments) BuildServiceWithPublisher(
        bool isAvailable = true)
    {
        var glucose = new Mock<IGlucosePublisher>();
        var treatments = new Mock<ITreatmentPublisher>();
        var publisher = new Mock<IConnectorPublisher>();
        publisher.SetupGet(p => p.IsAvailable).Returns(isAvailable);
        publisher.SetupGet(p => p.Glucose).Returns(glucose.Object);
        publisher.SetupGet(p => p.Treatments).Returns(treatments.Object);

        var service = new TestConnectorService(
            new HttpClient(), Mock.Of<ILogger<TestConnectorService>>(), publisher.Object);
        return (service, publisher, glucose, treatments);
    }

    [Fact]
    public async Task GlucosePublishOriginAsync_NoPriorData_ReturnsBackfill()
    {
        // Arrange: null watermark = no prior glucose for this source = first-ever sync
        var (service, _, glucose, _) = BuildServiceWithPublisher();
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
        var (service, _, glucose, _) = BuildServiceWithPublisher();
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
        var (service, _, glucose, _) = BuildServiceWithPublisher();
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
        var (service, _, glucose, _) = BuildServiceWithPublisher(isAvailable: false);

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
        var (service, _, _, treatments) = BuildServiceWithPublisher();
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
        var (service, _, _, treatments) = BuildServiceWithPublisher();
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
        var (service, _, _, treatments) = BuildServiceWithPublisher();
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
}
