using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

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

    private AlertDeliveryService CreateService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_broadcast.Object);
        services.AddSingleton(_inApp.Object);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<InAppProvider>();

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return new AlertDeliveryService(
            _factory.AsNocturneFactory(),
            Mock.Of<ITenantAccessor>(a => a.TenantId == Tenant),
            _broadcast.Object,
            provider,
            NullLogger<AlertDeliveryService>.Instance);
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
}
