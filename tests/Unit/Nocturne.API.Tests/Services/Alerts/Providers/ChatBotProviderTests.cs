using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
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

    private const string TestInstanceKey = "test-instance-key";
    private const string TestTenantSlug = "acme";

    private static ChatBotProvider CreateProvider(
        MockHttpMessageHandler handler,
        string? webUrl = "https://web.example.com",
        string? baseUrl = null,
        string? instanceKey = TestInstanceKey,
        string? tenantSlug = TestTenantSlug)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };

        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        httpClientFactoryMock
            .Setup(f => f.CreateClient("ChatBot"))
            .Returns(httpClient);

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["WEB_URL"]).Returns(webUrl);
        configMock.Setup(c => c["BaseUrl"]).Returns(baseUrl);
        configMock.Setup(c => c["INSTANCE_KEY"]).Returns(instanceKey);

        var tenantAccessorMock = new Mock<ITenantAccessor>();
        if (tenantSlug is not null)
        {
            tenantAccessorMock
                .Setup(a => a.Context)
                .Returns(new TenantContext(Guid.NewGuid(), tenantSlug, "Acme", true));
        }

        var logger = NullLoggerFactory.Instance.CreateLogger<ChatBotProvider>();

        return new ChatBotProvider(
            httpClientFactoryMock.Object,
            configMock.Object,
            tenantAccessorMock.Object,
            logger);
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
    public async Task SendAsync_SendsInstanceKeyServiceCredential()
    {
        // Arrange -- the dispatch route is internet-reachable through the gateway and
        // authenticates the caller on the instance-key digest plus the service marker.
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var provider = CreateProvider(handler);

        // Act
        await provider.SendAsync(Guid.NewGuid(), ChannelType.DiscordDm, "u1", CreateTestPayload(), CancellationToken.None);

        // Assert
        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.Headers
            .GetValues(ServiceNames.Headers.InstanceKey).Single()
            .Should().Be(HashUtils.Sha256Hex(TestInstanceKey));
        handler.CapturedRequest.Headers
            .GetValues(ServiceNames.Headers.InstanceService).Single()
            .Should().Be(ServiceNames.NocturneApi);
    }

    [Fact]
    public async Task SendAsync_NamesTargetTenantInBody()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var provider = CreateProvider(handler);

        // Act
        await provider.SendAsync(Guid.NewGuid(), ChannelType.DiscordDm, "u1", CreateTestPayload(), CancellationToken.None);

        // Assert -- the route scopes its API calls to this slug rather than a forwarded host
        handler.CapturedContent.Should().NotBeNullOrEmpty();
        JsonDocument.Parse(handler.CapturedContent!).RootElement
            .GetProperty("tenantSlug").GetString()
            .Should().Be(TestTenantSlug);
    }

    [Fact]
    public async Task SendAsync_SkipsDispatch_WhenInstanceKeyNotConfigured()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var provider = CreateProvider(handler, instanceKey: null);

        // Act
        await provider.SendAsync(Guid.NewGuid(), ChannelType.DiscordDm, "u1", CreateTestPayload(), CancellationToken.None);

        // Assert -- an unauthenticated dispatch would be rejected, so none is sent
        handler.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_SkipsDispatch_WhenNoTenantResolved()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var provider = CreateProvider(handler, tenantSlug: null);

        // Act
        await provider.SendAsync(Guid.NewGuid(), ChannelType.DiscordDm, "u1", CreateTestPayload(), CancellationToken.None);

        // Assert
        handler.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_PostsToTheInternalWebAddress()
    {
        // Arrange -- WEB_URL is the web app's address on the deployment's internal
        // network, so the dispatch never leaves it.
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var provider = CreateProvider(handler, webUrl: "http://nocturne-web:5173");

        // Act
        await provider.SendAsync(Guid.NewGuid(), ChannelType.DiscordDm, "u1", CreateTestPayload(), CancellationToken.None);

        // Assert
        handler.CapturedRequest!.RequestUri!.ToString()
            .Should().Be("http://nocturne-web:5173/api/v4/bot/dispatch");
    }

    [Fact]
    public async Task SendAsync_DoesNotFallBackToThePublicBaseUrl()
    {
        // Arrange -- only the public base URL is configured. An edge proxy in front of
        // it strips X-Instance-Key/X-Instance-Service (the /api/** prefix is
        // internet-reachable), so dispatching there would 401 and drop the alert.
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var provider = CreateProvider(handler, webUrl: "", baseUrl: "https://nocturne.run");

        // Act
        await provider.SendAsync(Guid.NewGuid(), ChannelType.DiscordDm, "u1", CreateTestPayload(), CancellationToken.None);

        // Assert -- no request at all, rather than one to the public URL
        handler.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_LogsWarning_WhenWebUrlNotConfigured()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var provider = CreateProvider(handler, webUrl: "", baseUrl: "");

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
