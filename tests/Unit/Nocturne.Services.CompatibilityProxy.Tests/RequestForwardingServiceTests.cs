using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Models.Compatibility;
using Nocturne.API.Services.Compatibility;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Xunit;

namespace Nocturne.Services.CompatibilityProxy.Tests.Unit;

public class RequestForwardingServiceTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ICorrelationService> _correlationServiceMock;
    private readonly NightscoutCircuitBreaker _circuitBreaker;
    private readonly ILogger<RequestForwardingService> _logger;

    public RequestForwardingServiceTests()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _correlationServiceMock = new Mock<ICorrelationService>();
        _circuitBreaker = new NightscoutCircuitBreaker();
        _logger = new LoggerFactory().CreateLogger<RequestForwardingService>();
    }

    private RequestForwardingService CreateService(
        CompatibilityProxyConfiguration? proxyConfig = null,
        NightscoutConnectorConfiguration? nightscoutConfig = null)
    {
        var options = Options.Create(proxyConfig ?? new CompatibilityProxyConfiguration());
        return new RequestForwardingService(
            _httpClientFactoryMock.Object,
            options,
            nightscoutConfig ?? new NightscoutConnectorConfiguration(),
            _circuitBreaker,
            _correlationServiceMock.Object,
            _logger
        );
    }

    [Fact]
    public async Task ForwardToNightscoutAsync_CircuitBreakerOpen_ShouldReturnNull()
    {
        // Arrange — trip the circuit breaker
        for (int i = 0; i < 10; i++)
            _circuitBreaker.RecordFailure();

        var service = CreateService();
        var request = new ClonedRequest { Method = "GET", Path = "/api/v1/entries" };

        // Act
        var result = await service.ForwardToNightscoutAsync(request);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ForwardToNightscoutAsync_NoUrl_ShouldReturnErrorResponse()
    {
        // Arrange — connector config with empty URL
        var nightscoutConfig = new NightscoutConnectorConfiguration { Url = "" };
        var service = CreateService(nightscoutConfig: nightscoutConfig);
        var request = new ClonedRequest { Method = "GET", Path = "/api/v1/entries" };

        // Act
        var result = await service.ForwardToNightscoutAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Nightscout URL not configured", result.ErrorMessage);
    }

    [Fact]
    public void FilterSensitiveErrorMessage_ContainsSensitiveData_ShouldRedactFieldNames()
    {
        // Arrange
        var config = new CompatibilityProxyConfiguration
        {
            Redaction = new RedactionSettings
            {
                SensitiveFields = new List<string> { "custom_field" },
            },
        };

        var service = CreateService(proxyConfig: config);
        var errorMessage = "Authentication failed with api_secret=12345 and token=abcdef";

        // Use reflection to access the private FilterSensitiveErrorMessage method
        var filterMethod = typeof(RequestForwardingService).GetMethod(
            "FilterSensitiveErrorMessage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );

        // Act
        var filtered = (string)filterMethod!.Invoke(service, new object[] { errorMessage })!;

        // Assert
        Assert.Contains("[REDACTED]", filtered);
        Assert.DoesNotContain("api_secret", filtered);
        Assert.DoesNotContain("token", filtered);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        }
    }

    [Theory]
    [InlineData("https://ns.example", "/api/v1/entries.json?count=10", "https://ns.example/api/v1/entries.json?count=10")]
    [InlineData("https://ns.example/nightscout", "/api/v1/entries.json?count=10", "https://ns.example/nightscout/api/v1/entries.json?count=10")]
    [InlineData("https://ns.example/nightscout/", "/api/v1/entries.json", "https://ns.example/nightscout/api/v1/entries.json")]
    [InlineData("https://ns.example/nightscout?token=synthetic-token", "/api/v1/status", "https://ns.example/nightscout/api/v1/status")]
    [InlineData("ns.example/nightscout", "/api/v1/status", "https://ns.example/nightscout/api/v1/status")]
    public async Task ForwardToNightscoutAsync_KeepsTheConfiguredSubPath(
        string nightscoutUrl, string path, string expected)
    {
        var handler = new RecordingHandler();
        _httpClientFactoryMock
            .Setup(f => f.CreateClient("NightscoutClient"))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        var service = CreateService(nightscoutConfig: new NightscoutConnectorConfiguration { Url = nightscoutUrl });

        var result = await service.ForwardToNightscoutAsync(new ClonedRequest { Method = "GET", Path = path });

        Assert.NotNull(result);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(expected, handler.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task ForwardToNightscoutAsync_DoesNotLeaveTheConfiguredBase()
    {
        var handler = new RecordingHandler();
        _httpClientFactoryMock
            .Setup(f => f.CreateClient("NightscoutClient"))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        var service = CreateService(nightscoutConfig: new NightscoutConnectorConfiguration { Url = "https://ns.example/nightscout" });

        var result = await service.ForwardToNightscoutAsync(
            new ClonedRequest { Method = "GET", Path = "/http://other.example/api/v1/status" });

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Null(handler.RequestUri);
    }
}
