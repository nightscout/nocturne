using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// A rule's low/high classification for scoped Do Not Disturb (ADR 0004), derived
/// by the shared engine's <c>classify</c> from the directional leaves of the
/// condition tree. <see cref="Composite"/> (the tree mixes low and high) and
/// <see cref="Undirected"/> (no directional leaf) are both "all-only" — silenced
/// only by a <see cref="DndScope.All"/> window; kept distinct so the classification
/// round-trips faithfully for audit.
/// </summary>
/// <seealso cref="DndScope"/>
[JsonConverter(typeof(JsonStringEnumConverter<RuleScopeClass>))]
public enum RuleScopeClass
{
    [EnumMember(Value = "low"), JsonStringEnumMemberName("low")]
    Low,

    [EnumMember(Value = "high"), JsonStringEnumMemberName("high")]
    High,

    [EnumMember(Value = "composite"), JsonStringEnumMemberName("composite")]
    Composite,

    [EnumMember(Value = "undirected"), JsonStringEnumMemberName("undirected")]
    Undirected,
}
