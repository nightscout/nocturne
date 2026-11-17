using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// What a <c>trackerUpdate</c> realtime event says happened to its tracker instance.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrackerRealtimeAction>))]
public enum TrackerRealtimeAction
{
    [EnumMember(Value = "create"), JsonStringEnumMemberName("create")]
    Create,

    [EnumMember(Value = "reopen"), JsonStringEnumMemberName("reopen")]
    Reopen,

    [EnumMember(Value = "delete"), JsonStringEnumMemberName("delete")]
    Delete,

    [EnumMember(Value = "complete"), JsonStringEnumMemberName("complete")]
    Complete,

    [EnumMember(Value = "ack"), JsonStringEnumMemberName("ack")]
    Ack,
}
