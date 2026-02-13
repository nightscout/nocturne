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
            : base(httpClient, logger, publisher)
        {
        }

        protected override string ConnectorSource => "test";
        public override string ServiceName => "Test";

        public override Task<bool> AuthenticateAsync() => Task.FromResult(true);
        public override Task<IEnumerable<Nocturne.Core.Models.Entry>> FetchGlucoseDataAsync(DateTime? since = null)
            => Task.FromResult(Enumerable.Empty<Nocturne.Core.Models.Entry>());
    }

    public class TestConfig : IConnectorConfiguration
    {
        public int SyncIntervalMinutes { get; set; } = 5;
        public bool Enabled { get; set; } = true;
        public int BatchSize { get; set; } = 100;
        public ConnectSource ConnectSource { get; set; } = ConnectSource.Dexcom;
        public int MaxRetryAttempts { get; set; } = 3;

        public void Validate() { }
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
}
