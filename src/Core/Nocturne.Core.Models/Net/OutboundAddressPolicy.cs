namespace Nocturne.Core.Models.Net;

/// <summary>
/// The address rules an outbound destination is held to.
/// </summary>
public enum OutboundAddressPolicy
{
    /// <summary>
    /// Every address must be publicly routable. For alert webhooks, which notify a third-party
    /// service on the internet: nothing private is a legitimate target.
    /// </summary>
    PubliclyRoutable,

    /// <summary>
    /// No address may be link-local. For member-supplied endpoints — a connector base URL, a
    /// Nightscout to migrate from, an OIDC issuer — that a self-hosted deployment legitimately puts
    /// on its own LAN or Docker network, so refusing the whole private range would break ordinary
    /// setups. Link-local is where the cloud metadata endpoints live, and nothing a member supplies
    /// has a reason to be there.
    /// </summary>
    NotLinkLocal,
}
