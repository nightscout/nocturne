using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.Alerts.Webhooks;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Shared.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Providers;

/// <summary>
/// Covers what a signature-verifying receiver sees for a real alert: a channel carrying a signing
/// secret must produce the same signed request the webhook test button produces, and a channel
/// without one must stay unsigned so receivers that never verified keep working.
/// </summary>
[Trait("Category", "Unit")]
public class WebhookProviderTests
{
    // An IP literal skips DNS in OutboundDestination, and the documentation range classifies as
    // publicly routable, so the send reaches the capturing handler.
    private const string Url = "https://203.0.113.10/hook";
    private const string PlaintextSecret = "not-a-real-secret";

    [Fact]
    public async Task SendAsync_signs_the_request_with_the_channels_secret()
    {
        var (provider, handler, encryption) = CreateProvider();

        await provider.SendAsync(Url, encryption.Encrypt(PlaintextSecret), Payload(), CancellationToken.None);

        var timestamp = long.Parse(handler.CapturedHeaders["X-Nocturne-Timestamp"]);
        handler.CapturedHeaders["X-Nocturne-Signature"].Should()
            .Be(WebhookSignature.Create(PlaintextSecret, handler.CapturedBody!, timestamp));
        handler.CapturedHeaders["X-Nocturne-Signature-Version"].Should().Be("v1");
    }

    [Fact]
    public async Task SendAsync_without_a_secret_sends_unsigned()
    {
        var (provider, handler, _) = CreateProvider();

        await provider.SendAsync(Url, null, Payload(), CancellationToken.None);

        handler.CapturedBody.Should().NotBeNull();
        handler.CapturedHeaders.Should().NotContainKey("X-Nocturne-Signature");
    }

    [Fact]
    public async Task SendAsync_fails_the_delivery_when_the_stored_secret_cannot_be_decrypted()
    {
        var (provider, handler, _) = CreateProvider();

        var send = () => provider.SendAsync(Url, "not-valid-ciphertext", Payload(), CancellationToken.None);

        await send.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not be decrypted*");
        handler.CapturedBody.Should().BeNull();
    }

    private static (WebhookProvider Provider, CapturingHandler Handler, ISecretEncryptionService Encryption)
        CreateProvider()
    {
        var handler = new CapturingHandler();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(f => f.CreateClient(WebhookRequestSender.HttpClientName))
            .Returns(new HttpClient(handler));

        var encryption = new SecretEncryptionService(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ServiceNames.ConfigKeys.InstanceKey] = "test-instance-key",
                })
                .Build(),
            NullLogger<SecretEncryptionService>.Instance);

        var provider = new WebhookProvider(
            new WebhookRequestSender(httpClientFactory.Object, NullLogger<WebhookRequestSender>.Instance),
            encryption,
            NullLogger<WebhookProvider>.Instance);

        return (provider, handler, encryption);
    }

    private static AlertPayload Payload() => new()
    {
        AlertType = AlertConditionType.Threshold,
        RuleName = "Low",
        Severity = AlertRuleSeverity.Warning,
        TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        GlucoseValue = 62,
        Trend = null,
        TrendRate = null,
        ReadingTimestamp = DateTime.UtcNow,
        SubjectName = "Subject",
        ExcursionId = Guid.Empty,
        InstanceId = Guid.Empty,
        ActiveExcursionCount = 1,
    };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }

        public Dictionary<string, string> CapturedHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Read inside the handler: the sender disposes its StringContent once the send returns.
            CapturedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            foreach (var (name, values) in request.Content.Headers)
            {
                CapturedHeaders[name] = string.Join(',', values);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
