using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Providers;

[Trait("Category", "Unit")]
public class ChatBotProviderTests
{
    private static AlertPayload CreateTestPayload() => new()
    {
        AlertType = AlertConditionType.Threshold,
        RuleName = "Low glucose",
        GlucoseValue = 55m,
        Trend = "Flat",
        TrendRate = -0.5m,
        ReadingTimestamp = DateTime.UtcNow,
        ExcursionId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        SubjectName = "Test",
        ActiveExcursionCount = 1,
        Severity = AlertRuleSeverity.Critical,
    };

    private static ChatBotProvider CreateProvider(
        MockHttpMessageHandler handler,
        string? webUrl = "https://web.example.com")
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };

        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        httpClientFactoryMock
            .Setup(f => f.CreateClient("ChatBot"))
            .Returns(httpClient);

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["WEB_URL"]).Returns(webUrl);

        var logger = NullLoggerFactory.Instance.CreateLogger<ChatBotProvider>();

        return new ChatBotProvider(httpClientFactoryMock.Object, configMock.Object, logger);
    }

    [Fact]
    public async Task SendAsync_PostsToCorrectUrl()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var provider = CreateProvider(handler, "https://web.example.com");

        // Act
        await provider.SendAsync(Guid.NewGuid(), ChannelType.DiscordDm, "user-1", CreateTestPayload(), CancellationToken.None);

        // Assert
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.RequestUri!.ToString()
            .Should().Be("https://web.example.com/api/v4/bot/dispatch");
        handler.CapturedRequest.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task SendAsync_IncludesDeliveryPayload()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var provider = CreateProvider(handler);
        var deliveryId = Guid.NewGuid();

        // Act
        await provider.SendAsync(deliveryId, ChannelType.SlackDm, "dest-1", CreateTestPayload(), CancellationToken.None);

        // Assert
        handler.CapturedContent.Should().NotBeNullOrEmpty();
        var doc = JsonDocument.Parse(handler.CapturedContent!);
        var root = doc.RootElement;

        // System.Text.Json uses camelCase by default
        root.GetProperty("deliveryId").GetGuid().Should().Be(deliveryId);
        root.GetProperty("channelType").GetString().Should().Be("slack_dm");
        root.GetProperty("destination").GetString().Should().Be("dest-1");
        root.TryGetProperty("payload", out _).Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_LogsWarning_WhenWebUrlNotConfigured()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var provider = CreateProvider(handler, webUrl: "");

        // Act -- should return early without throwing
        await provider.SendAsync(Guid.NewGuid(), ChannelType.DiscordDm, "u1", CreateTestPayload(), CancellationToken.None);

        // Assert -- no HTTP request was made
        handler.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_ThrowsOnHttpFailure()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(HttpStatusCode.InternalServerError);
        var provider = CreateProvider(handler);

        // Act
        var act = () => provider.SendAsync(
            Guid.NewGuid(), ChannelType.TelegramDm, "u1", CreateTestPayload(), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    /// <summary>
    /// Test handler that captures outgoing requests and returns a configurable response.
    /// </summary>
    internal class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public MockHttpMessageHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public HttpRequestMessage? CapturedRequest { get; private set; }
        public string? CapturedContent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            if (request.Content is not null)
                CapturedContent = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_statusCode);
        }
    }
}
