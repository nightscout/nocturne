using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Models.Compatibility;
using Nocturne.API.Services.Compatibility;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
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
    [InlineData("ns.example/nightscout", "/api/v1/status", "https://ns.example/nightscout/api/v1/status")]
    [InlineData("https://ns.example?token=abc", "/api/v1/status", "https://ns.example/api/v1/status?token=abc")]
    [InlineData("https://ns.example/nightscout?token=abc", "/api/v1/status", "https://ns.example/nightscout/api/v1/status?token=abc")]
    [InlineData("https://ns.example/nightscout/?token=abc&key=k", "/api/v1/entries.json?count=10", "https://ns.example/nightscout/api/v1/entries.json?count=10&token=abc&key=k")]
    public async Task ForwardToNightscoutAsync_KeepsTheConfiguredSubPathAndQuery(
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

    [Theory]
    [InlineData("token")]
    [InlineData("secret")]
    [InlineData("access_token")]
    public async Task ForwardToNightscoutAsync_SendsTheSiteQueryInPlaceOfTheCallersCredential(string credential)
    {
        var handler = new RecordingHandler();
        _httpClientFactoryMock
            .Setup(f => f.CreateClient("NightscoutClient"))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        var service = CreateService(nightscoutConfig: new NightscoutConnectorConfiguration { Url = "https://ns.example/?token=abc" });

        var result = await service.ForwardToNightscoutAsync(
            new ClonedRequest { Method = "GET", Path = $"/api/v1/entries.json?count=1&{credential}=caller" });

        Assert.NotNull(result);
        Assert.Equal("count=1&token=abc", handler.RequestUri?.Query.TrimStart('?'));
    }

    [Theory]
    [InlineData("/http://other.example/api/v1/status")]
    [InlineData("/../api/v1/status")]
    public async Task ForwardToNightscoutAsync_DoesNotLeaveTheConfiguredBase(string path)
    {
        var handler = new RecordingHandler();
        _httpClientFactoryMock
            .Setup(f => f.CreateClient("NightscoutClient"))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        var service = CreateService(nightscoutConfig: new NightscoutConnectorConfiguration { Url = "https://ns.example/nightscout?token=abc" });

        var result = await service.ForwardToNightscoutAsync(new ClonedRequest { Method = "GET", Path = path });

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal(NightscoutAddress.OutsideBaseMessage, result.ErrorMessage);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task ForwardToNightscoutAsync_ARefusedAddressIsNotARequest()
    {
        var handler = new RecordingHandler();
        _httpClientFactoryMock
            .Setup(f => f.CreateClient("NightscoutClient"))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        var service = CreateService(nightscoutConfig: new NightscoutConnectorConfiguration { Url = "ftp://ns.example?token=abc" });

        var result = await service.ForwardToNightscoutAsync(new ClonedRequest { Method = "GET", Path = "/api/v1/status" });

        Assert.NotNull(result);
        Assert.Equal(NightscoutAddress.InvalidUrlMessage, result.ErrorMessage);
        Assert.Null(handler.RequestUri);
    }

    private sealed class CapturingLogger : ILogger<RequestForwardingService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
                Lines.AddRange(values.Select(v => v.Value?.ToString() ?? string.Empty));
            if (exception is not null)
                Lines.Add(exception.ToString());
        }
    }

    [Theory]
    [InlineData("https://ns.example/nightscout?token=abc123")]
    [InlineData("https://ns.example/AGYLdvgzOeq?token=abc123&key=k")]
    public async Task ForwardToNightscoutAsync_NoLogLineCarriesTheSiteQuery(string nightscoutUrl)
    {
        var handler = new RecordingHandler();
        _httpClientFactoryMock
            .Setup(f => f.CreateClient("NightscoutClient"))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        var logger = new CapturingLogger();
        var service = new RequestForwardingService(
            _httpClientFactoryMock.Object,
            Options.Create(new CompatibilityProxyConfiguration()),
            new NightscoutConnectorConfiguration { Url = nightscoutUrl },
            _circuitBreaker,
            _correlationServiceMock.Object,
            logger);

        await service.ForwardToNightscoutAsync(new ClonedRequest { Method = "GET", Path = "/api/v1/status?token=caller456" });
        await service.ForwardToNightscoutAsync(new ClonedRequest { Method = "GET", Path = "/../status" });

        Assert.Contains("token=abc123", handler.RequestUri!.Query);
        Assert.NotEmpty(logger.Lines);
        Assert.All(logger.Lines, line =>
        {
            Assert.DoesNotContain("abc123", line);
            Assert.DoesNotContain("caller456", line);
            Assert.DoesNotContain("token=", line);
            Assert.DoesNotContain("AGYLdvgzOeq", line);
        });
    }
}
