using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Who recorded that they saw an alert, by the kind of access they had.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlertReceiptActorKind>))]
public enum AlertReceiptActorKind
{
    /// <summary>A tenant member, named by their subject.</summary>
    [EnumMember(Value = "member"), JsonStringEnumMemberName("member")]
    Member,

    /// <summary>A guest link session, named by its grant and the label it was given.</summary>
    [EnumMember(Value = "guest"), JsonStringEnumMemberName("guest")]
    Guest,

    /// <summary>The tenant's public share link.</summary>
    [EnumMember(Value = "public_link"), JsonStringEnumMemberName("public_link")]
    PublicLink,

    /// <summary>A clock face its owner let acknowledge, named by the face.</summary>
    [EnumMember(Value = "clock"), JsonStringEnumMemberName("clock")]
    Clock,
}
