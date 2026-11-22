using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Tests.Services.Alerts.Webhooks;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// A fire whose read of the tenant's members fails: the channels judged from their own row still
/// go out, and each channel that goes to a member is recorded failed with the reason, so no row is
/// left pending and an urgent low is not held back by the read.
/// </summary>
[Trait("Category", "Unit")]
public class AlertDeliveryServiceStoreFailureTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private const string Unread = "Not sent: the members of this site could not be read";

    private sealed class SharedInMemoryFactory(string dbName) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext()
        {
            var ctx = TestDbContextFactory.CreateInMemoryContext(dbName);
            ctx.TenantId = Tenant;
            return ctx;
        }
    }

    private readonly IDbContextFactory<NocturneDbContext> _factory =
        new SharedInMemoryFactory($"delivery_store_failure_{Guid.NewGuid()}");
    private readonly WebhookTestHarness.CapturingWebhookHandler _webhook = new();

    private static readonly AlertRuleChannelSnapshot Webhook =
        new(Guid.NewGuid(), Guid.NewGuid(), ChannelType.Webhook, WebhookTestHarness.Url, null, 0);
    private static readonly AlertRuleChannelSnapshot Device =
        new(Guid.NewGuid(), Guid.NewGuid(), ChannelType.DeviceAction, "prelude", null, 1);
    private static readonly AlertRuleChannelSnapshot InApp =
        new(Guid.NewGuid(), Guid.NewGuid(), ChannelType.InApp, Guid.NewGuid().ToString(), null, 2);
    private static readonly AlertRuleChannelSnapshot DirectMessage =
        new(Guid.NewGuid(), Guid.NewGuid(), ChannelType.DiscordDm, "123456789012345678", null, 3);
    private static readonly AlertRuleChannelSnapshot[] Channels = [Webhook, Device, InApp, DirectMessage];

    private AlertDeliveryService Service()
    {
        var members = new Mock<ITenantMembershipRepository>();
        members.Setup(m => m.ListLivePeopleAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NpgsqlException("The connection pool has been exhausted"));

        var services = new ServiceCollection()
            .AddSingleton(new WebhookProvider(
                WebhookTestHarness.Sender(_webhook), WebhookTestHarness.Encryption(), NullLogger<WebhookProvider>.Instance))
            .AddSingleton(new DeviceActionProvider(
                Mock.Of<ISignalRBroadcastService>(), new MemoryCache(new MemoryCacheOptions()),
                NullLogger<DeviceActionProvider>.Instance, TimeProvider.System))
            .BuildServiceProvider();

        return new AlertDeliveryService(
            _factory.AsNocturneFactory(),
            members.Object,
            Mock.Of<ITenantAccessor>(a => a.TenantId == Tenant),
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<IEngineAlarmRelay>(),
            services,
            NullLogger<AlertDeliveryService>.Instance,
            TimeProvider.System);
    }

    private static AlertPayload Payload(Guid instanceId) => new()
    {
        AlertType = AlertConditionType.Threshold,
        RuleName = "Urgent low",
        Severity = AlertRuleSeverity.Critical,
        TenantId = Tenant,
        GlucoseValue = 52,
        Trend = null,
        TrendRate = null,
        ReadingTimestamp = DateTime.UtcNow,
        SubjectName = "Synthetic",
        ExcursionId = Guid.NewGuid(),
        InstanceId = instanceId,
        ActiveExcursionCount = 1,
    };

    private async Task<Guid> SeedInstanceAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var excursion = new AlertExcursionEntity
        {
            Id = Guid.NewGuid(), TenantId = Tenant, AlertRuleId = Guid.NewGuid(), StartedAt = DateTime.UtcNow,
        };
        var instance = new AlertInstanceEntity
        {
            Id = Guid.NewGuid(), TenantId = Tenant, AlertExcursionId = excursion.Id, TriggeredAt = DateTime.UtcNow,
        };
        db.AlertExcursions.Add(excursion);
        db.AlertInstances.Add(instance);
        await db.SaveChangesAsync();
        return instance.Id;
    }

    private async Task<Dictionary<ChannelType, AlertDeliveryEntity>> DeliveriesAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.AlertDeliveries.IgnoreQueryFilters().ToDictionaryAsync(d => d.ChannelType);
    }

    private void ShouldHaveSentOnlyTheChannelsJudgedFromTheirRow(Dictionary<ChannelType, AlertDeliveryEntity> rows)
    {
        _webhook.CapturedBody.Should().NotBeNull("the webhook needs no read of the members");
        rows[ChannelType.Webhook].Status.Should().Be("delivered");
        rows[ChannelType.DeviceAction].Status.Should().Be("delivered");
        rows[ChannelType.InApp].Should().Match<AlertDeliveryEntity>(d => d.Status == "failed" && d.LastError == Unread);
        rows[ChannelType.DiscordDm].Should().Match<AlertDeliveryEntity>(d => d.Status == "failed" && d.LastError == Unread);
        rows.Values.Should().NotContain(d => d.Status == "pending");
    }

    [Fact]
    public async Task A_fire_sends_what_needs_no_read_and_fails_what_goes_to_a_member()
    {
        var instanceId = await SeedInstanceAsync();

        await Service().DispatchAsync(instanceId, Channels, Payload(instanceId), CancellationToken.None);

        ShouldHaveSentOnlyTheChannelsJudgedFromTheirRow(await DeliveriesAsync());
    }

    [Fact]
    public async Task A_test_fire_sends_what_needs_no_read_and_fails_what_goes_to_a_member()
    {
        await Service().TestFireAsync(Guid.NewGuid(), Channels, Payload(Guid.Empty), CancellationToken.None);

        ShouldHaveSentOnlyTheChannelsJudgedFromTheirRow(await DeliveriesAsync());
    }

    [Fact]
    public async Task A_dry_run_sends_what_needs_no_read_and_reports_what_goes_to_a_member()
    {
        var notSent = await Service().TestFireDryRunAsync(Channels, Payload(Guid.Empty), CancellationToken.None);

        _webhook.CapturedBody.Should().NotBeNull();
        notSent.Should().BeEquivalentTo(new[]
        {
            new AlertChannelNotSent(2, ChannelType.InApp, AlertChannelState.NoRecipient),
            new AlertChannelNotSent(3, ChannelType.DiscordDm, AlertChannelState.NoRecipient),
        });
    }
}
