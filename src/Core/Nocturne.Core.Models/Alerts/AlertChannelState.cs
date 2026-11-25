using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>Whether an alert rule's channel delivers when the rule fires.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlertChannelState>))]
public enum AlertChannelState
{
    /// <summary>The channel is sent to.</summary>
    [EnumMember(Value = "active"), JsonStringEnumMemberName("active")]
    Active,

    /// <summary>
    /// The member the channel goes to cannot receive: an <c>in_app</c> channel's member, or the
    /// member a direct message's linked chat account belongs to, has left the site or lost access.
    /// </summary>
    [EnumMember(Value = "no_recipient"), JsonStringEnumMemberName("no_recipient")]
    NoRecipient,

    /// <summary>A channel whose destination was typed in, past the end of the access of the member who added it.</summary>
    [EnumMember(Value = "stopped"), JsonStringEnumMemberName("stopped")]
    Stopped,
}

/// <summary>A channel a test fire did not send, by its place in the channel list it fired through.</summary>
/// <param name="Index">The channel's position in that list.</param>
public sealed record AlertChannelNotSent(int Index, ChannelType ChannelType, AlertChannelState State);
