using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// Verifies the connector honours the configured MaxRetryAttempts when a fetch hits a
/// retryable error, rather than always using the framework default.
/// </summary>
public class NightscoutConnectorRetryTests
{
    private static NightscoutConnectorService CreateService(
        HttpMessageHandler handler,
        int? maxRetryAttempts = null)
    {
        var config = new NightscoutConnectorConfiguration
        {
            Url = "https://nightscout.example.com",
            ApiSecret = "test-secret",
        };
        if (maxRetryAttempts.HasValue)
            config.MaxRetryAttempts = maxRetryAttempts.Value;

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(config.Url),
        };

        return new NightscoutConnectorService(
            httpClient,
            new ConnectorServerResolver<NightscoutConnectorConfiguration>(null, null, null),
            Mock.Of<ILogger<NightscoutConnectorService>>(),
            Mock.Of<IRetryDelayStrategy>(),
            Mock.Of<IRateLimitingStrategy>(),
            new ConnectorRegistration<NightscoutConnectorConfiguration>(config, "Nightscout"));
    }

    [Fact]
    public async Task FetchGlucoseData_RetryableError_RetriesUpToConfiguredMaxRetryAttempts()
    {
        // Arrange: a connector configured to attempt 5 times, hitting a retryable 503 every time
        var handler = new CountingStatusHandler(HttpStatusCode.ServiceUnavailable);
        var service = CreateService(handler, maxRetryAttempts: 5);

        // Act: the fetch exhausts every attempt then surfaces the last error
        var act = async () => await service.FetchGlucoseDataAsync();

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        handler.CallCount.Should().Be(5, "MaxRetryAttempts should drive the number of attempts");
    }

    [Fact]
    public async Task FetchGlucoseData_DefaultConfig_AttemptsThreeTimes()
    {
        // Arrange: default config (MaxRetryAttempts defaults to 3) — locks in prior behaviour
        var handler = new CountingStatusHandler(HttpStatusCode.ServiceUnavailable);
        var service = CreateService(handler);

        // Act
        var act = async () => await service.FetchGlucoseDataAsync();

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        handler.CallCount.Should().Be(3, "the default of 3 attempts must be unchanged");
    }

    [Fact]
    public async Task FetchGlucoseData_ZeroMaxRetryAttempts_AttemptsExactlyOnce()
    {
        // Arrange: MaxRetryAttempts = 0 must still try once (clamped to a floor of 1),
        // not skip the fetch entirely
        var handler = new CountingStatusHandler(HttpStatusCode.ServiceUnavailable);
        var service = CreateService(handler, maxRetryAttempts: 0);

        // Act
        var act = async () => await service.FetchGlucoseDataAsync();

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        handler.CallCount.Should().Be(1, "0 retries means a single attempt, not zero");
    }

    /// <summary>
    /// Handler that returns a fixed status code for every request and counts the calls.
    /// </summary>
    private sealed class CountingStatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public CountingStatusHandler(HttpStatusCode status) => _status = status;

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent("service unavailable"),
            });
        }
    }
}
