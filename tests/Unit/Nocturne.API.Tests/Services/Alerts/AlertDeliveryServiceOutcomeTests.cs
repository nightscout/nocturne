using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The status <see cref="AlertDeliveryService"/> records for each delivery. A row marked
/// <c>delivered</c> is the only evidence that an alert reached someone, so a channel that
/// reached nobody must never be recorded that way.
/// </summary>
[Trait("Category", "Unit")]
public class AlertDeliveryServiceOutcomeTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid Member = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

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
        new SharedInMemoryFactory($"delivery_outcome_{Guid.NewGuid()}");
    private readonly Mock<ISignalRBroadcastService> _broadcast = new();
    private readonly Mock<IInAppNotificationService> _inApp = new();
    private readonly List<ServiceProvider> _providers = [];

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    private AlertDeliveryService CreateService(ILogger<AlertDeliveryService>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_broadcast.Object);
        services.AddSingleton(_inApp.Object);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<InAppProvider>();
        services.AddSingleton(new DeviceActionProvider(
            _broadcast.Object, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DeviceActionProvider>.Instance, TimeProvider.System));

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return new AlertDeliveryService(
            _factory.AsNocturneFactory(),
            new TenantMembershipRepository(_factory.AsNocturneFactory(), AccountLinkRoutes.Off),
            Mock.Of<ITenantAccessor>(a => a.TenantId == Tenant),
            _broadcast.Object,
            Mock.Of<IEngineAlarmRelay>(),
            provider,
            logger ?? NullLogger<AlertDeliveryService>.Instance,
            TimeProvider.System);
    }

    private async Task<Guid> SeedInstanceAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var excursion = new AlertExcursionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            AlertRuleId = Guid.NewGuid(),
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        var instance = new AlertInstanceEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            AlertExcursionId = excursion.Id,
            TriggeredAt = excursion.StartedAt,
        };
        db.AlertExcursions.Add(excursion);
        db.AlertInstances.Add(instance);
        await db.SaveChangesAsync();
        return instance.Id;
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

    private static AlertRuleChannelSnapshot Channel(ChannelType type, string destination) =>
        new(Guid.NewGuid(), Guid.NewGuid(), type, destination, null, 0);

    private async Task<AlertDeliveryEntity> SingleDeliveryAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.AlertDeliveries.IgnoreQueryFilters().SingleAsync();
    }

    private async Task<AlertDeliveryEntity> DispatchOneAsync(AlertRuleChannelSnapshot channel)
    {
        var instanceId = await SeedInstanceAsync();
        await CreateService().DispatchAsync(instanceId, [channel], Payload(instanceId), CancellationToken.None);
        return await SingleDeliveryAsync();
    }

    [Fact]
    public async Task A_web_push_channel_is_recorded_failed_because_no_browser_receives_it()
    {
        var delivery = await DispatchOneAsync(Channel(ChannelType.WebPush, string.Empty));

        delivery.Status.Should().Be("failed");
        delivery.DeliveredAt.Should().BeNull();
        delivery.LastError.Should().NotBeNullOrWhiteSpace();
        _broadcast.Verify(b => b.BroadcastAlertEventAsync("alert_push", It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task A_test_fire_through_a_web_push_channel_is_recorded_failed()
    {
        await CreateService().TestFireAsync(
            Guid.NewGuid(), [Channel(ChannelType.WebPush, string.Empty)], Payload(Guid.Empty), CancellationToken.None);

        (await SingleDeliveryAsync()).Status.Should().Be("failed");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_in_app_channel_with_no_recipient_is_recorded_failed(string destination)
    {
        var delivery = await DispatchOneAsync(Channel(ChannelType.InApp, destination));

        delivery.Status.Should().Be("failed");
        delivery.DeliveredAt.Should().BeNull();
        delivery.LastError.Should().Be("In-app channel names no recipient");
        _inApp.VerifyNoOtherCalls();
    }

    private async Task AddMemberAsync(Guid tenantId, Guid subjectId, List<string>? permissions = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (!await db.Subjects.AnyAsync(s => s.Id == subjectId))
            db.Subjects.Add(new SubjectEntity { Id = subjectId, Name = "Member" });
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.NewGuid(), TenantId = tenantId, SubjectId = subjectId, DirectPermissions = permissions ?? ["*"],
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_in_app_channel_naming_a_member_who_holds_no_access_creates_nothing_and_is_recorded_failed()
    {
        await AddMemberAsync(Tenant, Member, permissions: []);

        var delivery = await DispatchOneAsync(Channel(ChannelType.InApp, Member.ToString()));

        delivery.Status.Should().Be("failed");
        _inApp.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task An_in_app_channel_naming_someone_outside_the_tenant_creates_nothing_and_is_recorded_failed()
    {
        await AddMemberAsync(Guid.NewGuid(), Member);

        var delivery = await DispatchOneAsync(Channel(ChannelType.InApp, Member.ToString()));

        delivery.Status.Should().Be("failed");
        delivery.LastError.Should().Be("In-app recipient is not a member of this site who holds any access");
        _inApp.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task An_in_app_channel_naming_a_member_creates_their_notification_and_is_recorded_delivered()
    {
        await AddMemberAsync(Tenant, Member);

        var delivery = await DispatchOneAsync(Channel(ChannelType.InApp, Member.ToString()));

        delivery.Status.Should().Be("delivered");
        _inApp.Verify(n => n.CreateNotificationAsync(
            Member.ToString(),
            InAppProvider.NotificationType,
            It.IsAny<string>(),
            It.IsAny<NotificationCategory?>(),
            It.IsAny<NotificationUrgency?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<List<NotificationActionDto>?>(),
            It.IsAny<ResolutionConditions?>(),
            It.IsAny<Dictionary<string, object>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_fire_cancelled_during_a_send_logs_no_error_and_tries_no_further_channel()
    {
        await AddMemberAsync(Tenant, Member);
        using var shutdown = new CancellationTokenSource();
        _broadcast.Setup(b => b.BroadcastDeviceActionAsync(It.IsAny<DeviceActionIntent>()))
            .Returns(async () =>
            {
                await shutdown.CancelAsync();
                shutdown.Token.ThrowIfCancellationRequested();
            });
        var logger = new ListLogger<AlertDeliveryService>();
        var instanceId = await SeedInstanceAsync();

        var fire = () => CreateService(logger).DispatchAsync(
            instanceId,
            [Channel(ChannelType.DeviceAction, DeviceKinds.Companion), Channel(ChannelType.InApp, Member.ToString())],
            Payload(instanceId),
            shutdown.Token);

        await fire.Should().ThrowAsync<OperationCanceledException>();
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
        _inApp.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_channel_that_times_out_on_its_own_is_recorded_failed_and_the_next_channel_is_still_sent()
    {
        await AddMemberAsync(Tenant, Member);
        _broadcast.Setup(b => b.BroadcastDeviceActionAsync(It.IsAny<DeviceActionIntent>()))
            .ThrowsAsync(new TaskCanceledException("The request timed out"));
        var device = Channel(ChannelType.DeviceAction, DeviceKinds.Companion);
        var inApp = Channel(ChannelType.InApp, Member.ToString());
        var instanceId = await SeedInstanceAsync();

        await CreateService().DispatchAsync(instanceId, [device, inApp], Payload(instanceId), CancellationToken.None);

        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.AlertDeliveries.IgnoreQueryFilters().ToDictionaryAsync(d => d.AlertRuleChannelId!.Value);
        rows[device.Id].Status.Should().Be("failed");
        rows[inApp.Id].Status.Should().Be("delivered");
    }

    [Fact]
    public async Task A_channel_type_with_no_provider_is_recorded_failed_rather_than_left_pending()
    {
        var delivery = await DispatchOneAsync(Channel(ChannelType.Telegram, "12345"));

        delivery.Status.Should().Be("failed");
        delivery.LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_rule_with_no_channels_leaves_an_anchor_that_does_not_claim_delivery()
    {
        var instanceId = await SeedInstanceAsync();

        await CreateService().DispatchAsync(instanceId, [], Payload(instanceId), CancellationToken.None);

        var anchor = await SingleDeliveryAsync();
        anchor.Status.Should().NotBe("delivered");
        anchor.DeliveredAt.Should().BeNull();
    }

    [Fact]
    public async Task A_test_fire_of_a_rule_with_no_channels_does_not_claim_delivery()
    {
        await CreateService().TestFireAsync(Guid.NewGuid(), [], Payload(Guid.Empty), CancellationToken.None);

        (await SingleDeliveryAsync()).Status.Should().NotBe("delivered");
    }

    [Fact]
    public async Task A_notice_that_a_rule_cannot_be_evaluated_records_no_device_action_delivery()
    {
        var instanceId = await SeedInstanceAsync();

        await CreateService().DispatchAsync(
            instanceId, [Channel(ChannelType.DeviceAction, "companion")],
            Payload(instanceId) with { Kind = AlertPayloadKind.Unevaluable, GlucoseValue = null },
            CancellationToken.None);

        var delivery = await SingleDeliveryAsync();
        delivery.ChannelType.Should().NotBe(ChannelType.DeviceAction, "an actuation is a glucose alarm on the device");
        delivery.Status.Should().Be("failed", "the notice reached no one");
        delivery.LastError.Should().Contain("No channel of the rule carries");
    }

    [Theory]
    [InlineData(AlertPayloadKind.Unevaluable, "No channel of the rule carries a notice that it cannot be evaluated")]
    [InlineData(AlertPayloadKind.Held, "No channel of the rule carries a notice that it has waited for usable data")]
    [InlineData(AlertPayloadKind.HeldClosed, "No channel of the rule carries a notice that it has waited for usable data")]
    public async Task A_notice_with_only_device_action_channels_names_its_own_kind(AlertPayloadKind kind, string error)
    {
        var instanceId = await SeedInstanceAsync();

        await CreateService().DispatchAsync(
            instanceId, [Channel(ChannelType.DeviceAction, "companion")],
            Payload(instanceId) with { Kind = kind, GlucoseValue = null },
            CancellationToken.None);

        (await SingleDeliveryAsync()).LastError.Should().Be(error);
    }

    [Fact]
    public async Task A_notice_that_a_rule_cannot_be_evaluated_is_not_broadcast_to_the_alerts_hub()
    {
        var instanceId = await SeedInstanceAsync();

        await CreateService().DispatchAsync(
            instanceId, [Channel(ChannelType.InApp, Member.ToString())],
            Payload(instanceId) with { Kind = AlertPayloadKind.Unevaluable, GlucoseValue = null },
            CancellationToken.None);

        _broadcast.Verify(b => b.BroadcastAlertEventAsync("alert_dispatch", It.IsAny<object>()), Times.Never);
        (await SingleDeliveryAsync()).ChannelType.Should().Be(ChannelType.InApp);
    }

    [Fact]
    public async Task A_test_fire_through_a_stopped_channel_is_recorded_failed_with_the_stop()
    {
        var stopsAt = DateTime.UtcNow.AddMinutes(-1);
        var stopped = Channel(ChannelType.DiscordChannel, "1234567890123456789") with
        {
            AddedBySubjectId = Member,
            StopsAt = stopsAt,
        };

        await CreateService().TestFireAsync(Guid.NewGuid(), [stopped], Payload(Guid.Empty), CancellationToken.None);

        var delivery = await SingleDeliveryAsync();
        delivery.IsTest.Should().BeTrue();
        delivery.Status.Should().Be("failed");
        delivery.LastError.Should().StartWith($"Stopped at {stopsAt:O}");
    }

    [Fact]
    public async Task A_test_fire_through_a_direct_message_no_member_has_linked_is_recorded_failed_with_no_recipient()
    {
        await CreateService().TestFireAsync(
            Guid.NewGuid(), [Channel(ChannelType.DiscordDm, "1234567890123456789")], Payload(Guid.Empty), CancellationToken.None);

        var delivery = await SingleDeliveryAsync();
        delivery.Status.Should().Be("failed");
        delivery.LastError.Should().StartWith("No recipient");
    }

    [Fact]
    public async Task A_dry_run_reports_a_channel_it_did_not_send_and_records_nothing()
    {
        var notSent = await CreateService().TestFireDryRunAsync(
            [Channel(ChannelType.WebPush, string.Empty), Channel(ChannelType.DiscordDm, "1234567890123456789")],
            Payload(Guid.Empty), CancellationToken.None);

        notSent.Should().Equal(new AlertChannelNotSent(1, ChannelType.DiscordDm, AlertChannelState.NoRecipient));
        await using var db = await _factory.CreateDbContextAsync();
        (await db.AlertDeliveries.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_test_fire_of_a_saved_rule_reports_the_channel_it_did_not_send_and_still_sends_to_the_rest()
    {
        await AddMemberAsync(Tenant, Member);
        var stopped = Channel(ChannelType.DiscordChannel, "1234567890123456789") with
        {
            AddedBySubjectId = Member,
            StopsAt = DateTime.UtcNow.AddMinutes(-1),
        };

        var fired = await CreateService().TestFireAsync(
            Guid.NewGuid(), [Channel(ChannelType.InApp, Member.ToString()), stopped], Payload(Guid.Empty), CancellationToken.None);

        fired.NotSent.Should().Equal(new AlertChannelNotSent(1, ChannelType.DiscordChannel, AlertChannelState.Stopped));
        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.AlertDeliveries.IgnoreQueryFilters().Where(d => d.AlertInstanceId == fired.InstanceId).ToListAsync();
        rows.Single(d => d.ChannelType == ChannelType.InApp).Status.Should().Be("delivered");
        rows.Single(d => d.ChannelType == ChannelType.DiscordChannel).Status.Should().Be("failed");
    }

    [Fact]
    public async Task A_test_fire_of_a_saved_rule_whose_channels_all_deliver_reports_none_not_sent()
    {
        await AddMemberAsync(Tenant, Member);

        var fired = await CreateService().TestFireAsync(
            Guid.NewGuid(), [Channel(ChannelType.InApp, Member.ToString())], Payload(Guid.Empty), CancellationToken.None);

        fired.NotSent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_test_fire_of_a_saved_rule_and_a_dry_run_report_the_same_channels_not_sent()
    {
        var channels = new[]
        {
            Channel(ChannelType.WebPush, string.Empty),
            Channel(ChannelType.DiscordDm, "1234567890123456789"),
        };

        var fired = await CreateService().TestFireAsync(Guid.NewGuid(), channels, Payload(Guid.Empty), CancellationToken.None);
        var dryRun = await CreateService().TestFireDryRunAsync(channels, Payload(Guid.Empty), CancellationToken.None);

        fired.NotSent.Should().Equal(dryRun).And.ContainSingle();
    }
}
