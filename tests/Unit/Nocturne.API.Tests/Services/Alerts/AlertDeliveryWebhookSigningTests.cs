using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.Alerts.Webhooks;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Tests.Services.Alerts.Webhooks;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// Pins the whole carriage of a webhook channel's signing secret on a real fire: the stored
/// ciphertext must survive the repository's snapshot projection and the delivery service's
/// provider dispatch, and arrive at the receiver as a signature over the plaintext. Every link
/// is load-bearing — dropping the secret at any one of them reproduces the unsigned real alert
/// that a verifying receiver rejects, while the test button keeps passing.
/// </summary>
[Trait("Category", "Unit")]
public class AlertDeliveryWebhookSigningTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private const string PlaintextSecret = "not-a-real-secret";

    [Fact]
    public async Task Stored_channel_secret_reaches_the_receiver_as_a_signature_over_the_plaintext()
    {
        var handler = new WebhookTestHarness.CapturingWebhookHandler();
        var encryption = WebhookTestHarness.Encryption();
        var (service, channels) = await CreateSeamAsync(handler, encryption, encryption.Encrypt(PlaintextSecret));

        await service.DispatchAsync(Guid.CreateVersion7(), channels, Payload(), CancellationToken.None);

        handler.Signature.Should()
            .Be(WebhookSignature.Create(PlaintextSecret, handler.CapturedBody!, handler.Timestamp));
    }

    [Fact]
    public async Task Channel_without_a_stored_secret_reaches_the_receiver_unsigned()
    {
        var handler = new WebhookTestHarness.CapturingWebhookHandler();
        var encryption = WebhookTestHarness.Encryption();
        var (service, channels) = await CreateSeamAsync(handler, encryption, storedSecret: null);

        await service.DispatchAsync(Guid.CreateVersion7(), channels, Payload(), CancellationToken.None);

        handler.CapturedBody.Should().NotBeNull();
        handler.Signature.Should().BeNull();
    }

    /// <summary>
    /// Seeds a webhook channel, then reads it back through the same repository projection the
    /// orchestrator uses, so the snapshot handed to the delivery service is the production one
    /// rather than a hand-built stand-in.
    /// </summary>
    private static async Task<(AlertDeliveryService Service, IReadOnlyList<AlertRuleChannelSnapshot> Channels)>
        CreateSeamAsync(
            WebhookTestHarness.CapturingWebhookHandler handler,
            ISecretEncryptionService encryption,
            string? storedSecret)
    {
        var factory = new SharedInMemoryFactory($"webhook_signing_{Guid.NewGuid()}");
        var ruleId = Guid.CreateVersion7();

        await using (var seed = factory.CreateDbContext())
        {
            seed.TenantId = Tenant;
            seed.AlertRuleChannels.Add(new AlertRuleChannelEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = Tenant,
                AlertRuleId = ruleId,
                ChannelType = ChannelType.Webhook,
                Destination = WebhookTestHarness.Url,
                Secret = storedSecret,
                SortOrder = 0,
            });
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var channels = await new AlertRepository(factory)
            .GetChannelsForRuleAsync(Tenant, ruleId, CancellationToken.None);

        var provider = new WebhookProvider(
            WebhookTestHarness.Sender(handler), encryption, NullLogger<WebhookProvider>.Instance);
        var services = new ServiceCollection();
        services.AddSingleton(provider);

        var service = new AlertDeliveryService(
            factory,
            Mock.Of<ITenantAccessor>(a => a.TenantId == Tenant),
            Mock.Of<ISignalRBroadcastService>(),
            services.BuildServiceProvider(),
            NullLogger<AlertDeliveryService>.Instance);

        return (service, channels);
    }

    private sealed class SharedInMemoryFactory(string dbName) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() =>
            TestDbContextFactory.CreateInMemoryContext(dbName);
    }

    private static AlertPayload Payload() => new()
    {
        AlertType = AlertConditionType.Threshold,
        RuleName = "Low",
        Severity = AlertRuleSeverity.Warning,
        TenantId = Tenant,
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
