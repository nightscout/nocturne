using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Which alerts a Do Not Disturb window silences (ADR 0004): <see cref="Lows"/>,
/// <see cref="Highs"/>, or <see cref="All"/>. Matched against a rule's
/// <see cref="RuleScopeClass"/>. An <see cref="All"/> window is tenant-wide DND —
/// it also drives the <c>do_not_disturb</c> condition — whereas <see cref="Lows"/>
/// and <see cref="Highs"/> feed the suppression gate only.
/// </summary>
/// <seealso cref="RuleScopeClass"/>
[JsonConverter(typeof(JsonStringEnumConverter<DndScope>))]
public enum DndScope
{
    [EnumMember(Value = "lows"), JsonStringEnumMemberName("lows")]
    Lows,

    [EnumMember(Value = "highs"), JsonStringEnumMemberName("highs")]
    Highs,

    [EnumMember(Value = "all"), JsonStringEnumMemberName("all")]
    All,
}
