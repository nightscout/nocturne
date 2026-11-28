using FluentAssertions;
using Nocturne.Core.Models.ClientDevices;
using Xunit;

namespace Nocturne.Core.Models.Tests.ClientDevices;

[Trait("Category", "Unit")]
public class DeviceNotificationMirrorTests
{
    private static InAppNotificationDto Dto(
        string type,
        NotificationUrgency urgency,
        NotificationCategory category) => new()
    {
        Type = type,
        Urgency = urgency,
        Category = category,
    };

    [Fact]
    public void Qualifies_excludes_alert_firing()
    {
        // Alerts reach devices via device_action, never the mirror.
        DeviceNotificationMirror
            .Qualifies(Dto("alert.firing", NotificationUrgency.Urgent, NotificationCategory.Alert))
            .Should().BeFalse();
    }

    public static TheoryData<string> LabelKeys()
    {
        var data = new TheoryData<string>();
        foreach (var label in Enum.GetValues<NotificationLabel>())
            data.Add(label.ToWireString());
        return data;
    }

    [Theory]
    [MemberData(nameof(LabelKeys))]
    public void Qualifies_excludes_a_notification_whose_title_is_a_label_key(string key)
    {
        var notification = Dto("any.type", NotificationUrgency.Urgent, NotificationCategory.Alert);
        notification.Title = key;
        DeviceNotificationMirror.Qualifies(notification).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(LabelKeys))]
    public void Qualifies_excludes_a_notification_whose_subtitle_is_a_label_key_under_a_text_title(string key)
    {
        var notification = Dto("any.type", NotificationUrgency.Urgent, NotificationCategory.Alert);
        notification.Title = "A rule name";
        notification.Subtitle = key;
        DeviceNotificationMirror.Qualifies(notification).Should().BeFalse();
    }

    [Theory]
    [InlineData("Your API key has full access. Create per-device keys with least privilege.")]
    [InlineData("Membership request from a person")]
    [InlineData("Confirm you ate \"Toast\" at 8:15 AM")]
    [InlineData("breakfast · 40g carbs · via MyFitnessPal")]
    [InlineData("Rotate your API key")]
    [InlineData("Hello")]
    [InlineData("please add me")]
    public void Qualifies_mirrors_literal_text(string text)
    {
        var notification = Dto("api-key-rotation", NotificationUrgency.Info, NotificationCategory.ActionRequired);
        notification.Title = text;
        notification.Subtitle = text;
        DeviceNotificationMirror.Qualifies(notification).Should().BeTrue();
    }

    [Fact]
    public void Qualifies_excludes_bare_informational()
    {
        DeviceNotificationMirror
            .Qualifies(Dto("meal_matching.suggested_match", NotificationUrgency.Info, NotificationCategory.Informational))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(NotificationUrgency.Warn)]
    [InlineData(NotificationUrgency.Hazard)]
    [InlineData(NotificationUrgency.Urgent)]
    public void Qualifies_by_urgency_warn_and_above(NotificationUrgency urgency)
    {
        DeviceNotificationMirror
            .Qualifies(Dto("membership.requested", urgency, NotificationCategory.Informational))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(NotificationCategory.Alert)]
    [InlineData(NotificationCategory.ActionRequired)]
    public void Qualifies_by_category_even_at_info_urgency(NotificationCategory category)
    {
        DeviceNotificationMirror
            .Qualifies(Dto("api-key-rotation", NotificationUrgency.Info, category))
            .Should().BeTrue();
    }
}
