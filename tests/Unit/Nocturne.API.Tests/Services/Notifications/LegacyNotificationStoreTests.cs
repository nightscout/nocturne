using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Notifications;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services.Notifications;

[Trait("Category", "Unit")]
public class LegacyNotificationStoreTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Other = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    [Fact]
    public void AdminNotification_SameMessageCountsAgain()
    {
        var store = new LegacyNotificationStore(_time);

        store.AddAdminNotification(Tenant, new AdminNotification { Title = "t", Message = "m" }).Should().Be(1);
        store.AddAdminNotification(Tenant, new AdminNotification { Title = "t", Message = "m" }).Should().Be(2);

        store.ListAdminNotifications(Tenant).Should().ContainSingle().Which.Count.Should().Be(2);
    }

    [Fact]
    public void AdminNotification_NonPersistentGoesAfterNineHoursAndPersistentStays()
    {
        var store = new LegacyNotificationStore(_time);
        store.AddAdminNotification(Tenant, new AdminNotification { Title = "t", Message = "fleeting" });
        store.AddAdminNotification(Tenant, new AdminNotification { Title = "t", Message = "lasting", Persistent = true });

        _time.Advance(LegacyNotificationStore.AdminNotificationLifetime + TimeSpan.FromMinutes(1));

        store.ListAdminNotifications(Tenant).Should().ContainSingle().Which.Message.Should().Be("lasting");
    }

    [Fact]
    public void AdminNotification_ListedCopyDoesNotChangeTheStore()
    {
        var store = new LegacyNotificationStore(_time);
        store.AddAdminNotification(Tenant, new AdminNotification { Title = "t", Message = "m" });

        store.ListAdminNotifications(Tenant).Single().Count = 99;

        store.ListAdminNotifications(Tenant).Single().Count.Should().Be(1);
    }

    [Fact]
    public void AdminNotifications_AtTheCapGiveUpTheOldestOfThatTenantOnly()
    {
        var store = new LegacyNotificationStore(_time);
        store.AddAdminNotification(Other, new AdminNotification { Title = "t", Message = "other" });
        for (var i = 0; i < LegacyNotificationStore.MaxAdminNotificationsPerTenant; i++)
        {
            store.AddAdminNotification(Tenant, new AdminNotification { Title = "t", Message = $"m{i}", LastRecorded = _time.GetUtcNow().ToUnixTimeMilliseconds() });
            _time.Advance(TimeSpan.FromMilliseconds(1));
        }

        store.AddAdminNotification(Tenant, new AdminNotification { Title = "t", Message = "newest" });

        var held = store.ListAdminNotifications(Tenant);
        held.Should().HaveCount(LegacyNotificationStore.MaxAdminNotificationsPerTenant);
        held.Should().NotContain(n => n.Message == "m0");
        held.Should().Contain(n => n.Message == "newest");
        store.ListAdminNotifications(Other).Should().ContainSingle();
    }

    [Fact]
    public void AdminNotification_TitleAndMessageAreCut()
    {
        var store = new LegacyNotificationStore(_time);
        var longText = new string('x', LegacyNotificationStore.MaxTextLength + 50);

        store.AddAdminNotification(Tenant, new AdminNotification { Title = longText, Message = longText });
        store.AddAdminNotification(Tenant, new AdminNotification { Title = longText, Message = longText }).Should().Be(2);

        var held = store.ListAdminNotifications(Tenant).Should().ContainSingle().Subject;
        held.Title.Should().HaveLength(LegacyNotificationStore.MaxTextLength);
        held.Message.Should().HaveLength(LegacyNotificationStore.MaxTextLength);
    }
}
