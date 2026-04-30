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

/// <summary>
/// Wire-string mapping for <see cref="ExcursionCloseReason"/>, mirroring the
/// <see cref="EnumMember"/> values. Used by call sites that need to persist
/// the reason as text (e.g. the <c>alert_instances.resolution_reason</c>
/// column) rather than rely on JSON serialisation.
/// </summary>
public static class ExcursionCloseReasonExtensions
{
    public static string ToWireString(this ExcursionCloseReason reason) => reason switch
    {
        ExcursionCloseReason.Hysteresis => "hysteresis",
        ExcursionCloseReason.AutoResolve => "auto",
        ExcursionCloseReason.Manual => "manual",
        ExcursionCloseReason.RuleDisabled => "rule-disabled",
        _ => reason.ToString().ToLowerInvariant(),
    };
}
