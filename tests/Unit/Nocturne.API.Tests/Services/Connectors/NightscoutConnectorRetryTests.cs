using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
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
    private static (NightscoutConnectorService Service, NightscoutConnectorConfiguration Config) CreateService(
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

        var service = new NightscoutConnectorService(
            httpClient,
            new ConnectorServerResolver<NightscoutConnectorConfiguration>(null, null, null),
            Mock.Of<ILogger<NightscoutConnectorService>>(),
            Mock.Of<IRetryDelayStrategy>(),
            Mock.Of<IRateLimitingStrategy>(),
            new ConnectorRegistration<NightscoutConnectorConfiguration>(config, "Nightscout"));

        return (service, config);
    }

    private static SyncRequest GlucoseRequest() =>
        new()
        {
            From = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc),
            DataTypes = [SyncDataType.Glucose],
        };

    [Fact]
    public async Task SyncData_RetryableError_RetriesUpToConfiguredMaxRetryAttempts()
    {
        // Arrange: a connector configured to attempt 5 times, hitting a retryable 503 every time
        var handler = new AuthThenFailingHandler(HttpStatusCode.ServiceUnavailable);
        var (service, config) = CreateService(handler, maxRetryAttempts: 5);

        // Act: the fetch exhausts every attempt, and the sync reports the failure
        var result = await service.SyncDataAsync(GlucoseRequest(), config, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        handler.FetchAttempts.Should().Be(5, "MaxRetryAttempts should drive the number of attempts");
    }

    [Fact]
    public async Task SyncData_DefaultConfig_AttemptsThreeTimes()
    {
        // Arrange: default config (MaxRetryAttempts defaults to 3) — locks in prior behaviour
        var handler = new AuthThenFailingHandler(HttpStatusCode.ServiceUnavailable);
        var (service, config) = CreateService(handler);

        // Act
        var result = await service.SyncDataAsync(GlucoseRequest(), config, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        handler.FetchAttempts.Should().Be(3, "the default of 3 attempts must be unchanged");
    }

    [Fact]
    public async Task SyncData_ZeroMaxRetryAttempts_AttemptsExactlyOnce()
    {
        // Arrange: MaxRetryAttempts = 0 must still try once (clamped to a floor of 1),
        // not skip the fetch entirely
        var handler = new AuthThenFailingHandler(HttpStatusCode.ServiceUnavailable);
        var (service, config) = CreateService(handler, maxRetryAttempts: 0);

        // Act
        var result = await service.SyncDataAsync(GlucoseRequest(), config, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        handler.FetchAttempts.Should().Be(1, "0 retries means a single attempt, not zero");
    }

    /// <summary>
    /// Answers the connector's auth probe so the sync reaches the glucose fetch, then returns a
    /// fixed status for every subsequent request and counts those fetch attempts.
    /// </summary>
    private sealed class AuthThenFailingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private bool _authProbeAnswered;

        public AuthThenFailingHandler(HttpStatusCode status) => _status = status;

        public int FetchAttempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!_authProbeAnswered)
            {
                _authProbeAnswered = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
                });
            }

            FetchAttempts++;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent("service unavailable"),
            });
        }
    }
}
