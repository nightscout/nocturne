using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>What an <see cref="AlertPayload"/> tells its recipients.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlertPayloadKind>))]
public enum AlertPayloadKind
{
    /// <summary>The rule's condition became true.</summary>
    [EnumMember(Value = "excursion"), JsonStringEnumMemberName("excursion")]
    Excursion,

    /// <summary>
    /// The rule's stored condition cannot be evaluated, so the rule cannot fire until it is
    /// edited (docs/alerts/engine-semantics.md §1.4). Sent once per version of the condition.
    /// It carries no glucose value and is not a glucose alarm.
    /// </summary>
    [EnumMember(Value = "unevaluable"), JsonStringEnumMemberName("unevaluable")]
    Unevaluable,

    /// <summary>
    /// The rule has been waiting for missing data for the engine's hold bound (24 hours), so it
    /// cannot fire until the data comes back (docs/alerts/engine-semantics.md §6.4). Sent once per
    /// hold, with the facts it waits for and no glucose value.
    /// </summary>
    [EnumMember(Value = "held"), JsonStringEnumMemberName("held")]
    Held,

    /// <summary>
    /// As <see cref="Held"/>, for a rule whose alert was open when its hold reached the bound: the
    /// alert is closed because its data has been missing for 24 hours.
    /// </summary>
    [EnumMember(Value = "held_closed"), JsonStringEnumMemberName("held_closed")]
    HeldClosed,
}

/// <summary>Which payload kinds are notices rather than excursions.</summary>
public static class AlertPayloadKinds
{
    /// <summary>A notice about the rule, sent only through its channels: never an alarm.</summary>
    public static bool IsNotice(this AlertPayloadKind kind) => kind != AlertPayloadKind.Excursion;

    /// <summary>A notice that the rule's hold reached the bound.</summary>
    public static bool IsHeld(this AlertPayloadKind kind) =>
        kind is AlertPayloadKind.Held or AlertPayloadKind.HeldClosed;
}
