using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Models.Tests;

[Trait("Category", "Unit")]
public class NotificationLabelWireFormatTests
{
    private static readonly string[] FiledKeys =
    [
        "compression_low_detected", "compression_low_detected_subtitle", "review", "release_notes",
        "update_available", "instance_key_changed", "instance_key_changed_platform", "open_connectors",
        "open_alerts", "alert_unevaluable_subtitle", "alert_partly_unevaluable_subtitle",
        "alert_held_subtitle", "alert_held_closed_subtitle", "alert_channel_stopped",
        "alert_channel_stopped_subtitle", "alert_channel_no_recipient", "alert_channel_no_recipient_subtitle",
        "open_platform_settings", "release_installed", "team_renamed", "team_staff_joined",
        "team_staff_waiting", "share_link_rotated", "share_link_rotated_subtitle", "share_link_reset",
        "share_link_reset_subtitle", "device_clock_deviation", "device_clock_deviation_subtitle",
        "device_clock_ahead", "device_clock_ahead_subtitle", "declared_zone_changed",
        "declared_zone_changed_subtitle", "rejected_uploads", "rejected_uploads_subtitle",
        "raw_mmol_profile_values", "raw_mmol_profile_values_subtitle", "dismiss",
    ];

    private static readonly string[] FiledTypes =
    [
        "alert.firing", "alert.unevaluable", "alert.held", "alerts.channelNotSending", "team.staff_waiting",
        "team.staff_joined", "team.renamed", "system.update_available", "system.release_installed",
        "system.instance_key_changed", "system.instance_key_changed_platform", "data.rejected_uploads",
        "profile.raw_mmol_values", "meal_matching.suggested_match", "glucose.compression_low_review",
        "external.operator", "external.app", "tracker.unconfigured", "tracker.alert", "system.statistics_summary",
        "system.help_response", "glucose.predicted_low", "alerts.deviceClockAhead", "connector.deviceClockDeviation",
        "connector.declaredZoneChanged",
    ];

    [Fact]
    public void The_labels_are_exactly_the_keys_old_rows_carry()
    {
        Enum.GetValues<NotificationLabel>().Select(l => l.ToWireString())
            .Should().BeEquivalentTo(FiledKeys);
    }

    [Fact]
    public void A_label_reads_the_same_on_the_wire_as_in_a_stored_row()
    {
        foreach (var label in Enum.GetValues<NotificationLabel>())
            JsonSerializer.Serialize(label).Should().Be($"\"{label.ToWireString()}\"");
    }

    [Fact]
    public void The_types_are_the_strings_old_rows_carry()
    {
        Enum.GetValues<InAppNotificationType>().Select(t => t.ToWireString())
            .Should().BeEquivalentTo(FiledTypes);

        foreach (var type in Enum.GetValues<InAppNotificationType>())
            JsonSerializer.Serialize(type).Should().Be($"\"{type.ToWireString()}\"");
    }

    [Fact]
    public void No_two_types_share_a_wire_string()
    {
        Enum.GetValues<InAppNotificationType>().Select(t => t.ToWireString())
            .Should().OnlyHaveUniqueItems();
    }
}
