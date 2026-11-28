using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>The type of an in-app notification.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InAppNotificationType>))]
public enum InAppNotificationType
{
    /// <summary>An alert that fired.</summary>
    [EnumMember(Value = "alert.firing"), JsonStringEnumMemberName("alert.firing")]
    AlertFiring,

    /// <summary>The notice that a rule cannot be evaluated. It has no actions: there is no excursion to acknowledge.</summary>
    [EnumMember(Value = "alert.unevaluable"), JsonStringEnumMemberName("alert.unevaluable")]
    AlertUnevaluable,

    /// <summary>The notice that a rule's hold reached the engine's bound, without actions as <see cref="AlertUnevaluable"/>.</summary>
    [EnumMember(Value = "alert.held"), JsonStringEnumMemberName("alert.held")]
    AlertHeld,

    /// <summary>
    /// Tells an owner that a channel of an enabled rule does not send. It carries the channel's label,
    /// which can name a contact, and the member it reaches and the member who added it.
    /// </summary>
    [EnumMember(Value = "alerts.channelNotSending"), JsonStringEnumMemberName("alerts.channelNotSending")]
    AlertChannelNotSending,

    /// <summary>The notice that a team's staff wait for an owner's approval.</summary>
    [EnumMember(Value = "team.staff_waiting"), JsonStringEnumMemberName("team.staff_waiting")]
    TeamStaffWaiting,

    /// <summary>The notice that a team's staff newly reach the site.</summary>
    [EnumMember(Value = "team.staff_joined"), JsonStringEnumMemberName("team.staff_joined")]
    TeamStaffJoined,

    [EnumMember(Value = "team.renamed"), JsonStringEnumMemberName("team.renamed")]
    TeamRenamed,

    [EnumMember(Value = "system.update_available"), JsonStringEnumMemberName("system.update_available")]
    SystemUpdateAvailable,

    [EnumMember(Value = "system.release_installed"), JsonStringEnumMemberName("system.release_installed")]
    SystemReleaseInstalled,

    [EnumMember(Value = "system.instance_key_changed"), JsonStringEnumMemberName("system.instance_key_changed")]
    SystemInstanceKeyChanged,

    [EnumMember(Value = "system.instance_key_changed_platform"), JsonStringEnumMemberName("system.instance_key_changed_platform")]
    SystemInstanceKeyChangedPlatform,

    [EnumMember(Value = "data.rejected_uploads"), JsonStringEnumMemberName("data.rejected_uploads")]
    DataRejectedUploads,

    [EnumMember(Value = "profile.raw_mmol_values"), JsonStringEnumMemberName("profile.raw_mmol_values")]
    ProfileRawMmolValues,

    /// <summary>A suggested match. Registered in <c>BuiltInNotificationTemplates</c>, which supplies its category, icon and source.</summary>
    [EnumMember(Value = "meal_matching.suggested_match"), JsonStringEnumMemberName("meal_matching.suggested_match")]
    MealMatchingSuggestedMatch,

    [EnumMember(Value = "glucose.compression_low_review"), JsonStringEnumMemberName("glucose.compression_low_review")]
    GlucoseCompressionLowReview,

    /// <summary>The operator's notices.</summary>
    [EnumMember(Value = "external.operator"), JsonStringEnumMemberName("external.operator")]
    ExternalOperator,

    /// <summary>An app's notices.</summary>
    [EnumMember(Value = "external.app"), JsonStringEnumMemberName("external.app")]
    ExternalApp,

    [EnumMember(Value = "tracker.unconfigured"), JsonStringEnumMemberName("tracker.unconfigured")]
    TrackerUnconfigured,

    [EnumMember(Value = "tracker.alert"), JsonStringEnumMemberName("tracker.alert")]
    TrackerAlert,

    [EnumMember(Value = "system.statistics_summary"), JsonStringEnumMemberName("system.statistics_summary")]
    SystemStatisticsSummary,

    [EnumMember(Value = "system.help_response"), JsonStringEnumMemberName("system.help_response")]
    SystemHelpResponse,

    [EnumMember(Value = "glucose.predicted_low"), JsonStringEnumMemberName("glucose.predicted_low")]
    GlucosePredictedLow,

    [EnumMember(Value = "alerts.deviceClockAhead"), JsonStringEnumMemberName("alerts.deviceClockAhead")]
    AlertsDeviceClockAhead,

    [EnumMember(Value = "connector.deviceClockDeviation"), JsonStringEnumMemberName("connector.deviceClockDeviation")]
    ConnectorDeviceClockDeviation,

    [EnumMember(Value = "connector.declaredZoneChanged"), JsonStringEnumMemberName("connector.declaredZoneChanged")]
    ConnectorDeclaredZoneChanged,
}

public static class InAppNotificationTypeExtensions
{
    public static string ToWireString(this InAppNotificationType type) => EnumWireNames<InAppNotificationType>.Of(type);
}
