namespace Nocturne.Connectors.Core.Models;

/// <summary>
///     Something only the tenant can do for a connector, which a sync cannot. A connector whose
///     credential is a person's interactive sign-in — a code emailed to them, a browser flow —
///     cannot renew it in the background, so once it lapses every sync fails until the person
///     signs in again. Raising this from the sync lets the host tell them, ahead of the lapse
///     where the credential's expiry is known.
/// </summary>
/// <param name="Kind">What is being asked of the tenant.</param>
/// <param name="Deadline">When the credential stops working, if known.</param>
public sealed record ConnectorAttention(ConnectorAttentionKind Kind, DateTime? Deadline = null)
{
    public static ConnectorAttention ReconnectRequired() => new(ConnectorAttentionKind.ReconnectRequired);

    public static ConnectorAttention ReconnectSoon(DateTime deadline) => new(ConnectorAttentionKind.ReconnectSoon, deadline);
}

public enum ConnectorAttentionKind
{
    /// <summary>The credential still works but will stop by <see cref="ConnectorAttention.Deadline"/>.</summary>
    ReconnectSoon,

    /// <summary>The credential is missing, refused or lapsed; nothing syncs until the tenant signs in again.</summary>
    ReconnectRequired,
}
