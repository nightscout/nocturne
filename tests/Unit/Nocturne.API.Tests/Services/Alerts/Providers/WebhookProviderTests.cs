using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.Alerts.Webhooks;
using Nocturne.API.Tests.Services.Alerts.Webhooks;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
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
    private const string PlaintextSecret = "not-a-real-secret";

    [Fact]
    public async Task SendAsync_signs_the_request_with_the_channels_secret()
    {
        var (provider, handler, encryption) = CreateProvider();

        await provider.SendAsync(
            WebhookTestHarness.Url, encryption.Encrypt(PlaintextSecret), Payload(), CancellationToken.None);

        handler.Signature.Should()
            .Be(WebhookSignature.Create(PlaintextSecret, handler.CapturedBody!, handler.Timestamp));
        handler.CapturedHeaders["X-Nocturne-Signature-Version"].Should().Be("v1");
    }

    [Fact]
    public async Task SendAsync_without_a_secret_sends_unsigned()
    {
        var (provider, handler, _) = CreateProvider();

        await provider.SendAsync(WebhookTestHarness.Url, null, Payload(), CancellationToken.None);

        handler.CapturedBody.Should().NotBeNull();
        handler.Signature.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_fails_the_delivery_when_the_stored_secret_cannot_be_decrypted()
    {
        var (provider, handler, _) = CreateProvider();

        var send = () => provider.SendAsync(
            WebhookTestHarness.Url, "not-valid-ciphertext", Payload(), CancellationToken.None);

        await send.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not be decrypted*");
        handler.CapturedBody.Should().BeNull();
    }

    private static (WebhookProvider Provider, WebhookTestHarness.CapturingWebhookHandler Handler,
        ISecretEncryptionService Encryption) CreateProvider()
    {
        var handler = new WebhookTestHarness.CapturingWebhookHandler();
        var encryption = WebhookTestHarness.Encryption();

        var provider = new WebhookProvider(
            WebhookTestHarness.Sender(handler),
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
}
