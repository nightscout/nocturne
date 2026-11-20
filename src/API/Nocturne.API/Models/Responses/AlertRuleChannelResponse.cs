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
}
