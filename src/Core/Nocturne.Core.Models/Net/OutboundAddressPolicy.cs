namespace Nocturne.Core.Models.Net;

/// <summary>
/// The address rules an outbound destination is held to. The two sinks have different legitimate
/// ranges — see <see cref="OutboundDestination"/>.
/// </summary>
public enum OutboundAddressPolicy
{
    /// <summary>
    /// Every address must be publicly routable. For alert webhooks, which notify a third-party
    /// service on the internet: nothing private is a legitimate target.
    /// </summary>
    PubliclyRoutable,

    /// <summary>
    /// No address may be link-local. For connector base URLs and other member-supplied endpoints
    /// that a self-hosted deployment legitimately puts on its own network.
    /// </summary>
    NotLinkLocal,
}
