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
}
