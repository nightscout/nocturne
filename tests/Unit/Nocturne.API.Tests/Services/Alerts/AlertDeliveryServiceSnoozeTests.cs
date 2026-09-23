using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Hubs;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// <see cref="AlertDeliveryService.DispatchAsync"/> is the gate every alert delivery passes, so a
/// snoozed instance must leave no trace on any channel: no SignalR broadcast, no delivery row, no
/// provider call.
/// </summary>
[Trait("Category", "Unit")]
public class AlertDeliveryServiceSnoozeTests
{
    private static readonly Guid Tenant = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private sealed class SharedInMemoryFactory(string dbName) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext()
        {
            var ctx = TestDbContextFactory.CreateInMemoryContext(dbName);
            ctx.TenantId = Tenant;
            return ctx;
        }
    }

    private readonly IDbContextFactory<NocturneDbContext> _factory = new SharedInMemoryFactory($"delivery_snooze_{Guid.NewGuid()}");
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly Mock<IInAppNotificationService> _inApp = new();
    private readonly Mock<IHttpClientFactory> _httpClientFactory = new();
    private readonly Mock<IHubContext<HomeAssistantHub>> _haHub = new();

    private AlertDeliveryService CreateService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_broadcast.Object);
        services.AddSingleton(_inApp.Object);
        services.AddSingleton(_httpClientFactory.Object);
        services.AddSingleton(_haHub.Object);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(Mock.Of<ITenantAccessor>(a => a.TenantId == Tenant));
        services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<WebPushProvider>();
        services.AddSingleton<InAppProvider>();
        services.AddSingleton<DeviceActionProvider>();
        services.AddSingleton<ChatBotProvider>();
        services.AddSingleton<HomeAssistantProvider>();

        return new AlertDeliveryService(
            _factory,
            Mock.Of<ITenantAccessor>(a => a.TenantId == Tenant),
            _broadcast.Object,
            services.BuildServiceProvider(),
            NullLogger<AlertDeliveryService>.Instance);
    }

    private async Task<Guid> SeedInstanceAsync(DateTime? snoozedUntil)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var excursion = new AlertExcursionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            AlertRuleId = Guid.NewGuid(),
            StartedAt = DateTime.UtcNow.AddMinutes(-20),
        };
        var instance = new AlertInstanceEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            AlertExcursionId = excursion.Id,
            TriggeredAt = excursion.StartedAt,
            SnoozedUntil = snoozedUntil,
            SnoozeCount = snoozedUntil is null ? 0 : 1,
        };
        db.AlertExcursions.Add(excursion);
        db.AlertInstances.Add(instance);
        await db.SaveChangesAsync();
        return instance.Id;
    }

    private static AlertPayload Payload(Guid instanceId) => new()
    {
        AlertType = AlertConditionType.Threshold,
        RuleName = "Low",
        Severity = AlertRuleSeverity.Warning,
        TenantId = Tenant,
        GlucoseValue = 62,
        Trend = null,
        TrendRate = null,
        ReadingTimestamp = DateTime.UtcNow,
        SubjectName = "Synthetic",
        ExcursionId = Guid.NewGuid(),
        InstanceId = instanceId,
        ActiveExcursionCount = 1,
    };

    private static AlertRuleChannelSnapshot Channel(ChannelType type, string destination) =>
        new(Guid.NewGuid(), Guid.NewGuid(), type, destination, null, 0);

    private static IReadOnlyList<AlertRuleChannelSnapshot> EveryChannel() =>
    [
        Channel(ChannelType.WebPush, string.Empty),
        Channel(ChannelType.InApp, "user-1"),
        Channel(ChannelType.Webhook, "https://example.invalid/hook"),
        Channel(ChannelType.DiscordDm, "discord-user"),
        Channel(ChannelType.SlackChannel, "slack-channel"),
        Channel(ChannelType.HomeAssistant, "ha-instance"),
        Channel(ChannelType.DeviceAction, DeviceKinds.Companion),
    ];

    private async Task<List<AlertDeliveryEntity>> DeliveriesAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.AlertDeliveries.IgnoreQueryFilters().ToListAsync();
    }

    [Fact]
    public async Task Snoozed_instance_is_delivered_on_no_channel()
    {
        var instanceId = await SeedInstanceAsync(DateTime.UtcNow.AddMinutes(10));

        await CreateService().DispatchAsync(instanceId, EveryChannel(), Payload(instanceId), CancellationToken.None);

        (await DeliveriesAsync()).Should().BeEmpty();
        _broadcast.Verify(b => b.BroadcastAlertEventAsync(It.IsAny<string>(), It.IsAny<object>()), Times.Never,
            "neither alert_dispatch (web toast) nor alert_push (web push) may go out");
        _broadcast.Verify(b => b.BroadcastDeviceActionAsync(It.IsAny<DeviceActionIntent>()), Times.Never);
        _inApp.VerifyNoOtherCalls();
        _httpClientFactory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never, "chat bot must not be called");
        _haHub.Verify(h => h.Clients, Times.Never, "Home Assistant must not be called");
    }

    [Fact]
    public async Task Snoozed_instance_with_no_channels_leaves_no_audit_anchor_either()
    {
        var instanceId = await SeedInstanceAsync(DateTime.UtcNow.AddMinutes(10));

        await CreateService().DispatchAsync(instanceId, [], Payload(instanceId), CancellationToken.None);

        (await DeliveriesAsync()).Should().BeEmpty();
        _broadcast.Verify(b => b.BroadcastAlertEventAsync("alert_dispatch", It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task Lapsed_snooze_does_not_hold_back_delivery()
    {
        var instanceId = await SeedInstanceAsync(DateTime.UtcNow.AddSeconds(-1));

        await CreateService().DispatchAsync(instanceId, EveryChannel(), Payload(instanceId), CancellationToken.None);

        var deliveries = await DeliveriesAsync();
        deliveries.Select(d => d.ChannelType).Should().BeEquivalentTo(EveryChannel().Select(c => c.ChannelType));
        _broadcast.Verify(b => b.BroadcastAlertEventAsync("alert_dispatch", It.IsAny<object>()), Times.Once);
        _broadcast.Verify(b => b.BroadcastAlertEventAsync("alert_push", It.IsAny<object>()), Times.Once);
        _broadcast.Verify(b => b.BroadcastDeviceActionAsync(It.IsAny<DeviceActionIntent>()), Times.Once);
    }

    [Fact]
    public async Task Never_snoozed_instance_is_delivered()
    {
        var instanceId = await SeedInstanceAsync(snoozedUntil: null);

        await CreateService().DispatchAsync(
            instanceId, [Channel(ChannelType.WebPush, string.Empty)], Payload(instanceId), CancellationToken.None);

        (await DeliveriesAsync()).Should().ContainSingle(d => d.Status == "delivered");
    }
}
