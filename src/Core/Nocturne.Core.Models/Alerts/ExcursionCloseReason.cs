using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Why an alert excursion was closed. Stamped onto the resulting
/// <c>alert_instances.resolution_reason</c> column at resolve time so
/// downstream audits can distinguish between hysteresis-driven closes,
/// auto-resolve, manual closes, and rule-disable cleanup.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExcursionCloseReason>))]
public enum ExcursionCloseReason
{
    /// <summary>Hysteresis cool-down expired with the condition still cleared.</summary>
    [EnumMember(Value = "hysteresis"), JsonStringEnumMemberName("hysteresis")]
    Hysteresis,

    /// <summary>The rule's auto-resolve condition evaluated true.</summary>
    [EnumMember(Value = "auto"), JsonStringEnumMemberName("auto")]
    AutoResolve,

    /// <summary>Closed by an explicit user or system action.</summary>
    [EnumMember(Value = "manual"), JsonStringEnumMemberName("manual")]
    Manual,

    /// <summary>Closed because the owning rule was disabled or deleted.</summary>
    [EnumMember(Value = "rule-disabled"), JsonStringEnumMemberName("rule-disabled")]
    RuleDisabled,
}
