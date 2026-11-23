using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Models.Responses;

public class AlertRuleChannelResponse
{
    public Guid Id { get; set; }
    public ChannelType ChannelType { get; set; }
    public string Destination { get; set; } = string.Empty;
    public string? DestinationLabel { get; set; }
    public int SortOrder { get; set; }
    /// <summary>Channel-specific config (e.g. device_action capabilities). Null when unset.</summary>
    public object? Metadata { get; set; }
    /// <summary>Whether a webhook signing secret is stored for this channel. The secret itself is
    /// never returned.</summary>
    public bool HasSecret { get; set; }
    /// <summary>Who an <c>in_app</c> channel is sent to. Set only for in-app channels, and only
    /// for a caller who may edit alert rules.</summary>
    public InAppRecipientResponse? Recipient { get; set; }
    /// <summary>Whether the channel sends when the rule fires. Set, with the fields after it, only
    /// for a caller who may edit alert rules.</summary>
    public AlertChannelState? State { get; set; }
    /// <summary>The name of the member a channel in <see cref="AlertChannelState.NoRecipient"/>
    /// goes to, when known.</summary>
    public string? MemberName { get; set; }
    /// <summary>The name of the member who added a channel whose destination was typed in, when
    /// known.</summary>
    public string? AddedByName { get; set; }
    /// <summary>When a channel whose destination was typed in stops: the end of the access of the
    /// member who added it, or of the member who kept it. Null when it does not stop.</summary>
    public DateTime? StopsAt { get; set; }
}
