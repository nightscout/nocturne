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
