using System.ComponentModel.DataAnnotations;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Models.Requests.V4;

public class CreateAlertRuleChannelRequest
{
    public ChannelType ChannelType { get; set; }
    /// <summary>Channel-specific address: webhook URL, chat handle, device kind for device_action, etc. Empty for in-app/web-push.</summary>
    public string? Destination { get; set; }
    public string? DestinationLabel { get; set; }
    /// <summary>Channel-specific config, persisted as JSONB. For device_action: <c>{ "capabilities": ["notify", ...] }</c>.</summary>
    public object? Metadata { get; set; }
    /// <summary>
    /// Write-only HMAC signing secret for a <c>webhook</c> channel; the receiver verifies it
    /// against the <c>X-Nocturne-Signature</c> header. Omit to keep the secret stored against this
    /// channel type and destination — changing either is a new channel and carries no secret over.
    /// Send empty to clear it. At most 256 bytes once UTF-8 encoded. Never returned — the read side
    /// reports <see cref="Responses.AlertRuleChannelResponse.HasSecret"/> instead.
    /// </summary>
    [MaxLength(256)]
    public string? Secret { get; set; }
}
