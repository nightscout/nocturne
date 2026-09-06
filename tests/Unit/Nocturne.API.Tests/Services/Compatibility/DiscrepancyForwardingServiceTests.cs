using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Nocturne.API.Configuration;
using Nocturne.API.Models.Compatibility;
using Nocturne.API.Services.Compatibility;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Compatibility;

public class DiscrepancyForwardingServiceTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ILogger<DiscrepancyForwardingService>> _loggerMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;

    public DiscrepancyForwardingServiceTests()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _loggerMock = new Mock<ILogger<DiscrepancyForwardingService>>();
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
    }

    private DiscrepancyForwardingService CreateService(DiscrepancyForwardingSettings settings)
    {
        var config = new CompatibilityProxyConfiguration
        {
            DiscrepancyForwarding = settings
        };

        var options = Options.Create(config);

        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(DiscrepancyForwardingService.HttpClientName))
            .Returns(httpClient);

        return new DiscrepancyForwardingService(
            _httpClientFactoryMock.Object,
            options,
            _loggerMock.Object
        );
    }

    private ResponseComparisonResult CreateTestAnalysis(
        ResponseMatchType matchType = ResponseMatchType.MinorDifferences)
    {
        return new ResponseComparisonResult
        {
            TraceId = "test-correlation-123",
            ComparisonTimestamp = DateTimeOffset.UtcNow,
            OverallMatch = matchType,
            StatusCodeMatch = true,
            BodyMatch = false,
            Summary = "Test discrepancy",
            Discrepancies = new List<ResponseDiscrepancy>
            {
                new ResponseDiscrepancy
                {
                    Type = DiscrepancyType.StringValue,
                    Severity = DiscrepancySeverity.Minor,
                    Field = "testField",
                    NightscoutValue = "value1",
                    NocturneValue = "value2",
                    Description = "Values differ"
                }
            }
        };
    }

    [Fact]
    public async Task ForwardDiscrepancyAsync_WhenDisabled_ReturnsTrue()
    {
        // Arrange
        var settings = new DiscrepancyForwardingSettings
        {
            Enabled = false
        };
        var service = CreateService(settings);
        var analysis = CreateTestAnalysis();

        // Act
        var result = await service.ForwardDiscrepancyAsync(analysis, "GET", "/api/v1/entries");

        // Assert
        Assert.True(result);
        _httpClientFactoryMock.Verify(
            f => f.CreateClient(It.IsAny<string>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ForwardDiscrepancyAsync_WhenEndpointNotConfigured_SkipsRemoteForwardingGracefully()
    {
        // Arrange
        var settings = new DiscrepancyForwardingSettings
        {
            Enabled = true,
            EndpointUrl = ""
        };
        var service = CreateService(settings);
        var analysis = CreateTestAnalysis();

        // Act
        var result = await service.ForwardDiscrepancyAsync(analysis, "GET", "/api/v1/entries");

        // Assert - Returns true because no remote forward is needed when endpoint is empty
        Assert.True(result);
        // Verify no HTTP call was made
        _httpClientFactoryMock.Verify(
            f => f.CreateClient(It.IsAny<string>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ForwardDiscrepancyAsync_WhenBelowMinimumSeverity_ReturnsTrue()
    {
        // Arrange
        var settings = new DiscrepancyForwardingSettings
        {
            Enabled = true,
            EndpointUrl = "https://test.example.com",
            MinimumSeverity = DiscrepancySeverityLevel.Critical
        };
        var service = CreateService(settings);
        var analysis = CreateTestAnalysis(ResponseMatchType.MinorDifferences);

        // Act
        var result = await service.ForwardDiscrepancyAsync(analysis, "GET", "/api/v1/entries");

        // Assert
        Assert.True(result);
        _httpClientFactoryMock.Verify(
            f => f.CreateClient(It.IsAny<string>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ForwardDiscrepancyAsync_WhenSuccessful_ReturnsTrue()
    {
        // Arrange
        var settings = new DiscrepancyForwardingSettings
        {
            Enabled = true,
            EndpointUrl = "https://test.example.com",
            MinimumSeverity = DiscrepancySeverityLevel.Minor,
            TimeoutSeconds = 10
        };
        var service = CreateService(settings);
        var analysis = CreateTestAnalysis();

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":200}")
            });

        // Act
        var result = await service.ForwardDiscrepancyAsync(analysis, "GET", "/api/v1/entries");

        // Assert
        Assert.True(result);
        _httpMessageHandlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.RequestUri!.ToString().Contains("/api/v4/discrepancy/ingest")),
            ItExpr.IsAny<CancellationToken>()
        );
    }

    [Fact]
    public async Task ForwardDiscrepancyAsync_WhenClientError_DoesNotRetry()
    {
        // Arrange
        var settings = new DiscrepancyForwardingSettings
        {
            Enabled = true,
            EndpointUrl = "https://test.example.com",
            MinimumSeverity = DiscrepancySeverityLevel.Minor,
            TimeoutSeconds = 10,
            RetryAttempts = 3
        };
        var service = CreateService(settings);
        var analysis = CreateTestAnalysis();

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"status\":400}")
            });

        // Act
        var result = await service.ForwardDiscrepancyAsync(analysis, "GET", "/api/v1/entries");

        // Assert
        Assert.False(result);
        _httpMessageHandlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(), // Only one attempt - no retries on 4xx
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>()
        );
    }

    [Fact]
    public async Task ForwardDiscrepancyAsync_WithApiKey_IncludesAuthorizationHeader()
    {
        // Arrange
        var settings = new DiscrepancyForwardingSettings
        {
            Enabled = true,
            EndpointUrl = "https://test.example.com",
            ApiKey = "test-api-key",
            MinimumSeverity = DiscrepancySeverityLevel.Minor,
            TimeoutSeconds = 10
        };
        var service = CreateService(settings);
        var analysis = CreateTestAnalysis();

        HttpRequestMessage? capturedRequest = null;
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":200}")
            });

        // Act
        await service.ForwardDiscrepancyAsync(analysis, "GET", "/api/v1/entries");

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest!.Headers.Authorization);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization!.Scheme);
        Assert.Equal("test-api-key", capturedRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task ForwardDiscrepancyAsync_WithSourceId_IncludesSourceHeader()
    {
        // Arrange
        var settings = new DiscrepancyForwardingSettings
        {
            Enabled = true,
            EndpointUrl = "https://test.example.com",
            SourceId = "test-source",
            MinimumSeverity = DiscrepancySeverityLevel.Minor,
            TimeoutSeconds = 10
        };
        var service = CreateService(settings);
        var analysis = CreateTestAnalysis();

        HttpRequestMessage? capturedRequest = null;
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":200}")
            });

        // Act
        await service.ForwardDiscrepancyAsync(analysis, "GET", "/api/v1/entries");

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.Headers.Contains("X-Nocturne-Source"));
        Assert.Equal("test-source", capturedRequest.Headers.GetValues("X-Nocturne-Source").First());
    }
}
