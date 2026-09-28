using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.Chat;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// A member's mute of an excursion stops later escalation deliveries reaching that member, and only
/// that member.
/// </summary>
[Trait("Category", "Unit")]
public class AlertDeliveryServiceMuteTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private readonly string _dbName = $"delivery_mutes_{Guid.NewGuid()}";
    private readonly Mock<IInAppNotificationService> _notifications = new();

    private NocturneDbContext NewContext()
    {
        var db = TestDbContextFactory.CreateInMemoryContext(_dbName);
        db.TenantId = Tenant;
        return db;
    }

    private AlertDeliveryService CreateService()
    {
        var factory = new Mock<IDbContextFactory<NocturneDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => TestDbContextFactory.CreateInMemoryContext(_dbName));
        var services = new ServiceCollection()
            .AddSingleton(factory.Object)
            .AddSingleton<ChatIdentityDirectoryService>()
            .AddSingleton(_notifications.Object)
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>))
            .AddSingleton<InAppProvider>()
            .BuildServiceProvider();
        return new AlertDeliveryService(
            factory.Object,
            Mock.Of<ITenantAccessor>(a => a.TenantId == Tenant),
            Mock.Of<ISignalRBroadcastService>(),
            services,
            NullLogger<AlertDeliveryService>.Instance);
    }

    private static AlertPayload Payload(Guid excursionId) => new()
    {
        AlertType = AlertConditionType.Threshold,
        RuleName = "Urgent Low",
        Severity = AlertRuleSeverity.Critical,
        TenantId = Tenant,
        GlucoseValue = null,
        Trend = null,
        TrendRate = null,
        ReadingTimestamp = DateTime.UtcNow,
        SubjectName = "Patient",
        ExcursionId = excursionId,
        InstanceId = Guid.NewGuid(),
        ActiveExcursionCount = 1,
    };

    private static AlertRuleChannelSnapshot InApp(Guid subjectId) =>
        new(Guid.NewGuid(), Guid.NewGuid(), ChannelType.InApp, subjectId.ToString(), null, 0);

    private static AlertRuleChannelSnapshot DiscordDm(string discordUserId) =>
        new(Guid.NewGuid(), Guid.NewGuid(), ChannelType.DiscordDm, discordUserId, null, 0);

    private static ChatIdentityDirectoryEntry DiscordLink(string discordUserId, Guid subjectId) => new()
    {
        Id = Guid.NewGuid(),
        Platform = "discord",
        PlatformUserId = discordUserId,
        TenantId = Tenant,
        NocturneUserId = subjectId,
        Label = discordUserId,
        DisplayName = discordUserId,
    };

    [Fact]
    public async Task Escalation_skips_the_muted_members_in_app_delivery_and_still_reaches_everyone_else()
    {
        var excursionId = Guid.NewGuid();
        var muter = Guid.NewGuid();
        var other = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            seed.AlertExcursionMutes.Add(new AlertExcursionMuteEntity
            {
                Id = Guid.NewGuid(), TenantId = Tenant, SubjectId = muter, AlertExcursionId = excursionId,
            });
            await seed.SaveChangesAsync();
        }

        await CreateService().DispatchAsync(
            Guid.NewGuid(), [InApp(muter), InApp(other)], Payload(excursionId), CancellationToken.None);

        _notifications.Verify(NotificationFor(other), Times.Once);
        _notifications.Verify(NotificationFor(muter), Times.Never);
        await using var db = NewContext();
        (await db.AlertDeliveries.Select(d => d.Destination).ToListAsync())
            .Should().Equal(other.ToString());
    }

    [Fact]
    public async Task Escalation_skips_the_muted_members_linked_chat_dm_and_still_reaches_another_members()
    {
        const string mutersDiscord = "111111111111111111";
        const string othersDiscord = "222222222222222222";
        var excursionId = Guid.NewGuid();
        var muter = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            seed.AlertExcursionMutes.Add(new AlertExcursionMuteEntity
            {
                Id = Guid.NewGuid(), TenantId = Tenant, SubjectId = muter, AlertExcursionId = excursionId,
            });
            seed.ChatIdentityDirectory.AddRange(
                DiscordLink(mutersDiscord, muter), DiscordLink(othersDiscord, Guid.NewGuid()));
            await seed.SaveChangesAsync();
        }

        await CreateService().DispatchAsync(
            Guid.NewGuid(), [DiscordDm(mutersDiscord), DiscordDm(othersDiscord)], Payload(excursionId),
            CancellationToken.None);

        await using var db = NewContext();
        (await db.AlertDeliveries.Select(d => d.Destination).ToListAsync())
            .Should().Equal(othersDiscord);
    }

    [Fact]
    public async Task A_mute_of_another_excursion_does_not_skip_this_one()
    {
        var muter = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            seed.AlertExcursionMutes.Add(new AlertExcursionMuteEntity
            {
                Id = Guid.NewGuid(), TenantId = Tenant, SubjectId = muter, AlertExcursionId = Guid.NewGuid(),
            });
            await seed.SaveChangesAsync();
        }

        await CreateService().DispatchAsync(
            Guid.NewGuid(), [InApp(muter)], Payload(Guid.NewGuid()), CancellationToken.None);

        _notifications.Verify(NotificationFor(muter), Times.Once);
    }

    private static System.Linq.Expressions.Expression<Func<IInAppNotificationService, Task<Core.Models.InAppNotificationDto>>>
        NotificationFor(Guid subjectId) =>
        n => n.CreateNotificationAsync(
            subjectId.ToString(),
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
            It.IsAny<CancellationToken>());
}
