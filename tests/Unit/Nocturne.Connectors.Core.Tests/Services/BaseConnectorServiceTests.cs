using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
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
}
