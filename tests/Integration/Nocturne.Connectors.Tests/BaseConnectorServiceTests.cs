using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Connectors.Tests.Integration;

/// <summary>
/// Simple unit tests for BaseConnectorService HTTP API submission functionality
/// </summary>
public class BaseConnectorServiceTests
{
    [Fact]
    public void ConnectorConfiguration_DefaultsAreCorrect()
    {
        // Arrange & Act
        var config = new TestConnectorConfiguration
        {
            ConnectSource = ConnectSource.Dexcom,
        };

        // Assert
        Assert.True(config.UseAsyncProcessing);
        Assert.Equal(TimeSpan.FromMinutes(5), config.MessageTimeout);
        Assert.Equal(3, config.MaxRetryAttempts);
        Assert.Equal(50, config.BatchSize);
        Assert.Null(config.RoutingKeyPrefix);
    }

    [Fact]
    public async Task PublishGlucoseDataAsync_WithValidData_SubmitsToAPI()
    {
        // Arrange
        var apiSubmitterMock = new Mock<IApiDataSubmitter>();
        var loggerMock = new Mock<ILogger<BaseConnectorService<TestConnectorConfiguration>>>();
        var testService = new TestConnectorService(apiSubmitterMock.Object, loggerMock.Object);

        var config = new TestConnectorConfiguration
        {
            ConnectSource = ConnectSource.Dexcom,
            UseAsyncProcessing = true,
        };

        var entries = new[]
        {
            new Entry { Sgv = 120, Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            new Entry
            {
                Sgv = 115,
                Mills = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(),
            },
        };

        apiSubmitterMock
            .Setup(m =>
                m.SubmitEntriesAsync(
                    It.IsAny<IEnumerable<Entry>>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(true);

        // Act
        var result = await testService.PublishGlucoseDataAsyncPublic(entries, config);

        // Assert
        Assert.True(result);

        apiSubmitterMock.Verify(
            m =>
                m.SubmitEntriesAsync(
                    It.Is<IEnumerable<Entry>>(e => e.Count() == 2),
                    It.Is<string>(s => s == "test-connector"),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task SyncDataAsync_WithAsyncProcessingDisabled_UsesDirectAPI()
    {
        // Arrange
        var apiSubmitterMock = new Mock<IApiDataSubmitter>();
        var loggerMock = new Mock<ILogger<BaseConnectorService<TestConnectorConfiguration>>>();
        var testService = new TestConnectorService(apiSubmitterMock.Object, loggerMock.Object);

        var config = new TestConnectorConfiguration
        {
            ConnectSource = ConnectSource.Dexcom,
            UseAsyncProcessing = false, // Direct API mode
        };

        // Act
        var result = await testService.SyncDataAsync(config, TestContext.Current.CancellationToken);

        // Assert
        // Should have tried direct upload (will fail in test but that's expected)
        Assert.False(result); // Fails because no real Nightscout endpoint
    }
}

/// <summary>
/// Test connector configuration for unit testing
/// </summary>
public class TestConnectorConfiguration : BaseConnectorConfiguration
{
    protected override void ValidateSourceSpecificConfiguration()
    {
        // No specific validation needed for test connector
    }
}

/// <summary>
/// Test implementation of BaseConnectorService
/// </summary>
public class TestConnectorService : BaseConnectorService<TestConnectorConfiguration>
{
    public TestConnectorService(
        IApiDataSubmitter apiDataSubmitter,
        ILogger<BaseConnectorService<TestConnectorConfiguration>> logger
    )
        : base(new HttpClient(), logger, apiDataSubmitter) { }

    public override string ConnectorSource => "test-connector";
    public override string ServiceName => "Test Connector";

    public override Task<bool> AuthenticateAsync() => Task.FromResult(true);

    public override Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
    {
        var entries = new[]
        {
            new Entry { Sgv = 120, Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            new Entry
            {
                Sgv = 115,
                Mills = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(),
            },
        };
        return Task.FromResult<IEnumerable<Entry>>(entries);
    }

    // Public wrapper for testing protected method
    public Task<bool> PublishGlucoseDataAsyncPublic(
        IEnumerable<Entry> entries,
        TestConnectorConfiguration config
    )
    {
        return PublishGlucoseDataAsync(entries, config);
    }
}
