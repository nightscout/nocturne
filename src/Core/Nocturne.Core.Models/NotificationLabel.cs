using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// A title, subtitle or action label of an in-app notification that the backend names and the web
/// app words. The backend has no copy layer, so it stores the wire value and the web app maps it
/// to text; a value that is not a member renders as it is.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<NotificationLabel>))]
public enum NotificationLabel
{
    [EnumMember(Value = "compression_low_detected"), JsonStringEnumMemberName("compression_low_detected")]
    CompressionLowDetected,

    [EnumMember(Value = "compression_low_detected_subtitle"), JsonStringEnumMemberName("compression_low_detected_subtitle")]
    CompressionLowDetectedSubtitle,

    [EnumMember(Value = "review"), JsonStringEnumMemberName("review")]
    Review,

    [EnumMember(Value = "release_notes"), JsonStringEnumMemberName("release_notes")]
    ReleaseNotes,

    [EnumMember(Value = "update_available"), JsonStringEnumMemberName("update_available")]
    UpdateAvailable,

    [EnumMember(Value = "instance_key_changed"), JsonStringEnumMemberName("instance_key_changed")]
    InstanceKeyChanged,

    [EnumMember(Value = "instance_key_changed_platform"), JsonStringEnumMemberName("instance_key_changed_platform")]
    InstanceKeyChangedPlatform,

    [EnumMember(Value = "open_connectors"), JsonStringEnumMemberName("open_connectors")]
    OpenConnectors,

    [EnumMember(Value = "open_alerts"), JsonStringEnumMemberName("open_alerts")]
    OpenAlerts,

    [EnumMember(Value = "alert_unevaluable_subtitle"), JsonStringEnumMemberName("alert_unevaluable_subtitle")]
    AlertUnevaluableSubtitle,

    [EnumMember(Value = "alert_partly_unevaluable_subtitle"), JsonStringEnumMemberName("alert_partly_unevaluable_subtitle")]
    AlertPartlyUnevaluableSubtitle,

    [EnumMember(Value = "alert_held_subtitle"), JsonStringEnumMemberName("alert_held_subtitle")]
    AlertHeldSubtitle,

    [EnumMember(Value = "alert_held_closed_subtitle"), JsonStringEnumMemberName("alert_held_closed_subtitle")]
    AlertHeldClosedSubtitle,

    [EnumMember(Value = "alert_channel_stopped"), JsonStringEnumMemberName("alert_channel_stopped")]
    AlertChannelStopped,

    [EnumMember(Value = "alert_channel_stopped_subtitle"), JsonStringEnumMemberName("alert_channel_stopped_subtitle")]
    AlertChannelStoppedSubtitle,

    [EnumMember(Value = "alert_channel_no_recipient"), JsonStringEnumMemberName("alert_channel_no_recipient")]
    AlertChannelNoRecipient,

    [EnumMember(Value = "alert_channel_no_recipient_subtitle"), JsonStringEnumMemberName("alert_channel_no_recipient_subtitle")]
    AlertChannelNoRecipientSubtitle,

    [EnumMember(Value = "open_platform_settings"), JsonStringEnumMemberName("open_platform_settings")]
    OpenPlatformSettings,

    [EnumMember(Value = "release_installed"), JsonStringEnumMemberName("release_installed")]
    ReleaseInstalled,

    /// <summary>The title of the notice of a rename, which the web app words with the old and new team names.</summary>
    [EnumMember(Value = "team_renamed"), JsonStringEnumMemberName("team_renamed")]
    TeamRenamed,

    [EnumMember(Value = "team_staff_joined"), JsonStringEnumMemberName("team_staff_joined")]
    TeamStaffJoined,

    [EnumMember(Value = "team_staff_waiting"), JsonStringEnumMemberName("team_staff_waiting")]
    TeamStaffWaiting,

    [EnumMember(Value = "share_link_rotated"), JsonStringEnumMemberName("share_link_rotated")]
    ShareLinkRotated,

    [EnumMember(Value = "share_link_rotated_subtitle"), JsonStringEnumMemberName("share_link_rotated_subtitle")]
    ShareLinkRotatedSubtitle,

    /// <summary>Rows filed before the key was renamed to <see cref="ShareLinkRotated"/> still carry this value.</summary>
    [EnumMember(Value = "share_link_reset"), JsonStringEnumMemberName("share_link_reset")]
    ShareLinkReset,

    /// <summary>Rows filed before the key was renamed to <see cref="ShareLinkRotatedSubtitle"/> still carry this value.</summary>
    [EnumMember(Value = "share_link_reset_subtitle"), JsonStringEnumMemberName("share_link_reset_subtitle")]
    ShareLinkResetSubtitle,

    [EnumMember(Value = "device_clock_deviation"), JsonStringEnumMemberName("device_clock_deviation")]
    DeviceClockDeviation,

    [EnumMember(Value = "device_clock_deviation_subtitle"), JsonStringEnumMemberName("device_clock_deviation_subtitle")]
    DeviceClockDeviationSubtitle,

    [EnumMember(Value = "device_clock_ahead"), JsonStringEnumMemberName("device_clock_ahead")]
    DeviceClockAhead,

    [EnumMember(Value = "device_clock_ahead_subtitle"), JsonStringEnumMemberName("device_clock_ahead_subtitle")]
    DeviceClockAheadSubtitle,

    [EnumMember(Value = "declared_zone_changed"), JsonStringEnumMemberName("declared_zone_changed")]
    DeclaredZoneChanged,

    [EnumMember(Value = "declared_zone_changed_subtitle"), JsonStringEnumMemberName("declared_zone_changed_subtitle")]
    DeclaredZoneChangedSubtitle,

    [EnumMember(Value = "rejected_uploads"), JsonStringEnumMemberName("rejected_uploads")]
    RejectedUploads,

    [EnumMember(Value = "rejected_uploads_subtitle"), JsonStringEnumMemberName("rejected_uploads_subtitle")]
    RejectedUploadsSubtitle,

    [EnumMember(Value = "raw_mmol_profile_values"), JsonStringEnumMemberName("raw_mmol_profile_values")]
    RawMmolProfileValues,

    [EnumMember(Value = "raw_mmol_profile_values_subtitle"), JsonStringEnumMemberName("raw_mmol_profile_values_subtitle")]
    RawMmolProfileValuesSubtitle,

    [EnumMember(Value = "dismiss"), JsonStringEnumMemberName("dismiss")]
    Dismiss,
}

public static class NotificationLabelExtensions
{
    /// <summary>The string stored in a notification's title, subtitle or action label.</summary>
    public static string ToWireString(this NotificationLabel label) => EnumWireNames<NotificationLabel>.Of(label);
}
