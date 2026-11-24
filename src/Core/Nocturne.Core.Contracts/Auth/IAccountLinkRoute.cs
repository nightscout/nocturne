namespace Nocturne.Core.Contracts.Auth;

/// <summary>Whether this instance can mint an account link and deliver it to an address.</summary>
/// <remarks>
/// Answered by the delivery in the host and read by the data layer, which decides whether an
/// account that can reach a recovery link by email is locked out. Every reader takes it from the
/// container, so none decides it for itself.
/// </remarks>
public interface IAccountLinkRoute
{
    /// <summary>
    /// Whether a link can be minted and sent: a route to deliver it is configured, and so is the
    /// base domain its address is built on. The flows that send links, and the rule that counts a
    /// confirmed address as a way back in, read this one answer.
    /// </summary>
    bool IsConfigured { get; }
}
